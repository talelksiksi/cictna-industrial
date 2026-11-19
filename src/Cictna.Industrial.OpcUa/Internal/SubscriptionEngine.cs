using System.Threading.Channels;
using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Encoding;
using Microsoft.Extensions.Logging;

namespace Cictna.Industrial.OpcUa.Internal;

/// <summary>
/// The publish engine of one client: keeps Publish requests in flight, matches responses to
/// subscriptions, acknowledges them, recovers gaps with Republish, watches keep-alives, and moves
/// subscriptions to a new session (transfer, or recreate when transfer is not possible).
/// </summary>
/// <remarks>
/// <para>
/// Everything runs in one loop that takes messages from an inbox (responses, session up/down,
/// subscriptions added or removed, timer ticks), so the engine's state needs no locks. Service calls
/// it makes itself (Republish, TransferSubscriptions, recreate) are awaited inside the loop: later
/// responses wait in the inbox, which keeps notifications in sequence order.
/// </para>
/// <para>
/// Why our own engine instead of open62541's subscription API: it keeps all protocol decisions in
/// C# where they are testable, and lets a subscription outlive the native client (Phase 2 uses a
/// fresh native client for each connection, see docs/native-layer.md).
/// </para>
/// <para>
/// Sequence numbers (Part 4, 7.25): each subscription numbers its notification messages 1, 2, 3...
/// wrapping to 1 after 4294967295. A keep-alive carries the number the next message will get, so a
/// keep-alive or data message whose number is ahead of the expected one reveals a gap. Gaps are
/// filled with Republish from the server's retransmission queue (the numbers it still has are listed
/// in every Publish response); what it no longer has is counted as lost and reported.
/// </para>
/// </remarks>
internal sealed partial class SubscriptionEngine : IAsyncDisposable
{
    private const int MaxOutstandingPublishes = 5;
    private const int MaxRepublishPerGap = 1000;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RecoveryRetryInterval = TimeSpan.FromSeconds(5);
    private static readonly NodeId DataChangeType = new(SubscriptionEncodingIds.DataChangeNotification);
    private static readonly NodeId StatusChangeType = new(SubscriptionEncodingIds.StatusChangeNotification);

    private readonly ISubscriptionHost _host;
    private readonly ILogger _logger;
    private readonly Channel<object> _inbox = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly List<OpcUaSubscription> _subscriptions = [];
    private readonly Dictionary<uint, OpcUaSubscription> _byId = [];
    private readonly Timer _timer;
    private readonly Task _loop;
    private bool _sessionUp;
    private long _generation;
    private int _inFlight;
    private int _serverLimit = MaxOutstandingPublishes;
    private long _nextRecoveryAttempt;

    public SubscriptionEngine(ISubscriptionHost host)
    {
        _host = host;
        _logger = host.Logger;
        _loop = Task.Run(RunAsync);
        _timer = new Timer(_ => _inbox.Writer.TryWrite(Tick.Instance), null, TickInterval, TickInterval);
    }

    // ---- inputs (any thread) -----------------------------------------------------------------

    /// <summary>A session became active. Subscriptions from older sessions are transferred or recreated.</summary>
    public void SessionUp(long generation) => _inbox.Writer.TryWrite(new SessionUpMessage(generation));

    /// <summary>The session is gone; stop publishing until the next one.</summary>
    public void SessionDown() => _inbox.Writer.TryWrite(SessionDownMessage.Instance);

    /// <summary>Starts publishing for a subscription that now exists on the server.</summary>
    public void Register(OpcUaSubscription subscription) => _inbox.Writer.TryWrite(new AddMessage(subscription));

    /// <summary>Stops delivering to a subscription; completes when the engine no longer touches it.</summary>
    public async Task UnregisterAsync(OpcUaSubscription subscription)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inbox.Writer.TryWrite(new RemoveMessage(subscription, done)))
        {
            return; // engine stopped
        }
        await done.Task.WaitAsync(_stop.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    // ---- the loop ----------------------------------------------------------------------------

    private async Task RunAsync()
    {
        try
        {
            await foreach (var message in _inbox.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(message).ConfigureAwait(false);
                    TopUpPublishes();
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A bug or an unexpected server answer must not stop publishing for everyone.
                    LogEngineError(_logger, message.GetType().Name, ex);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // disposed
        }
    }

    private Task HandleAsync(object message)
    {
        switch (message)
        {
            case PublishDone done:
                return OnPublishDoneAsync(done);
            case Tick:
                return OnTickAsync();
            case SessionUpMessage up:
                _generation = up.Generation;
                _sessionUp = true;
                _inFlight = 0; // requests of the old session no longer count
                _serverLimit = MaxOutstandingPublishes;
                return RecoverAsync();
            case SessionDownMessage:
                _sessionUp = false;
                return Task.CompletedTask;
            case AddMessage add:
                _subscriptions.Add(add.Subscription);
                _byId[add.Subscription.Id] = add.Subscription;
                add.Subscription.LastActivity = Environment.TickCount64;
                return Task.CompletedTask;
            case RemoveMessage remove:
                _subscriptions.Remove(remove.Subscription);
                if (_byId.TryGetValue(remove.Subscription.Id, out var current) && current == remove.Subscription)
                {
                    _byId.Remove(remove.Subscription.Id);
                }
                remove.Done.TrySetResult();
                return Task.CompletedTask;
            default:
                return Task.CompletedTask;
        }
    }

    private List<OpcUaSubscription> Active() => _subscriptions.Where(s => s.Generation == _generation).ToList();

    // ---- publishing --------------------------------------------------------------------------

    /// <summary>
    /// Keeps enough Publish requests at the server that it can always answer at once: one more than
    /// there are subscriptions, at most five, fewer if the server said BadTooManyPublishRequests.
    /// </summary>
    private void TopUpPublishes()
    {
        if (!_sessionUp || _stop.IsCancellationRequested)
        {
            return;
        }
        var active = Active();
        if (active.Count == 0)
        {
            return;
        }
        var target = Math.Max(1, Math.Min(Math.Min(_serverLimit, MaxOutstandingPublishes), active.Count + 1));
        while (_inFlight < target)
        {
            SendPublish(active, target);
        }
    }

    private void SendPublish(List<OpcUaSubscription> active, int target)
    {
        var acks = new List<(OpcUaSubscription Subscription, uint Sequence)>();
        foreach (var s in active)
        {
            acks.AddRange(s.PendingAcks.Select(n => (s, n)));
            s.PendingAcks.Clear();
        }
        // The server holds Publish requests until it has something to send; with several queued, the
        // last one can wait about one keep-alive period per request ahead of it.
        var longest = active.Max(s => s.KeepAlivePeriod);
        var timeout = longest * (target + 1) + _host.RequestTimeout;
        var hint = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, uint.MaxValue);
        var request = new PublishRequest(new RequestHeader(hint),
            acks.Select(a => new SubscriptionAcknowledgement(a.Subscription.Id, a.Sequence)).ToArray()).Encode();
        _inFlight++;
        _ = SendPublishAsync(request, timeout, _generation, acks.ToArray());
    }

    private async Task SendPublishAsync(byte[] request, TimeSpan timeout, long generation, (OpcUaSubscription, uint)[] acks)
    {
        try
        {
            var bytes = await _host.SendAsync("Publish", SubscriptionEncodingIds.PublishRequest, SubscriptionEncodingIds.PublishResponse,
                request, timeout, _stop.Token).ConfigureAwait(false);
            _inbox.Writer.TryWrite(new PublishDone(generation, bytes, null, acks));
        }
        catch (Exception ex)
        {
            _inbox.Writer.TryWrite(new PublishDone(generation, null, ex, acks));
        }
    }

    private async Task OnPublishDoneAsync(PublishDone done)
    {
        if (done.Generation == _generation)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
        }
        if (done.Response is null)
        {
            Requeue(done.Acks);
            if (done.Error is OpcUaException { } ex && _sessionUp && done.Generation == _generation && ex.StatusCode != StatusCodes.BadTimeout)
            {
                LogPublishFailed(_logger, ex.StatusCode.Name);
            }
            return;
        }

        var response = ServiceCodec.Decode(done.Response, PublishResponse.Decode);
        var result = response.Header.ServiceResult;
        if (result.IsBad)
        {
            Requeue(done.Acks);
            if (result == StatusCodes.BadTooManyPublishRequests)
            {
                _serverLimit = Math.Max(1, _inFlight); // what the server is willing to hold
            }
            else if (result == StatusCodes.BadNoSubscription && done.Generation == _generation)
            {
                // The server has none, but we believe we have some: they expired or were deleted
                // behind our back. Create them again.
                foreach (var s in Active())
                {
                    await RecreateAsync(s).ConfigureAwait(false);
                }
            }
            else if (result != StatusCodes.BadTimeout)
            {
                LogPublishFailed(_logger, result.Name);
            }
            return;
        }

        OpcUaTelemetry.PublishResponses.Add(1);
        if (!_byId.TryGetValue(response.SubscriptionId, out var subscription) || subscription.Generation != _generation)
        {
            return; // deleted meanwhile, or from an older session
        }
        subscription.LastActivity = Environment.TickCount64;
        subscription.KeepAliveMissReported = false;
        await ProcessMessageAsync(subscription, response.NotificationMessage, response.AvailableSequenceNumbers).ConfigureAwait(false);
    }

    private void Requeue((OpcUaSubscription Subscription, uint Sequence)[] acks)
    {
        foreach (var (subscription, sequence) in acks)
        {
            if (_subscriptions.Contains(subscription))
            {
                subscription.PendingAcks.Add(sequence);
            }
        }
    }

    // ---- notification messages ---------------------------------------------------------------

    private async Task ProcessMessageAsync(OpcUaSubscription s, NotificationMessage message, uint[] available)
    {
        var expected = Next(s.LastSequenceNumber);
        if (message.IsKeepAlive)
        {
            // A keep-alive carries the next number to be used. Equal to the last delivered one is
            // also accepted: some servers send that.
            if (message.SequenceNumber != expected && message.SequenceNumber != s.LastSequenceNumber)
            {
                await FillGapAsync(s, expected, message.SequenceNumber, available).ConfigureAwait(false);
            }
            return;
        }
        if (s.LastSequenceNumber != 0 && IsAtOrBefore(message.SequenceNumber, s.LastSequenceNumber))
        {
            s.PendingAcks.Add(message.SequenceNumber); // a duplicate (already republished); just acknowledge
            return;
        }
        if (message.SequenceNumber != expected)
        {
            await FillGapAsync(s, expected, message.SequenceNumber, available).ConfigureAwait(false);
        }
        await DeliverAsync(s, message).ConfigureAwait(false);
    }

    /// <summary>Recovers messages <paramref name="from"/> up to (not including) <paramref name="until"/>.</summary>
    private async Task FillGapAsync(OpcUaSubscription s, uint from, uint until, uint[] available)
    {
        var count = Distance(from, until);
        if (count == 0 || count > int.MaxValue)
        {
            return; // not a gap but an old number; nothing sensible to recover
        }
        var listed = available.ToHashSet();
        long lost = 0;
        var sequence = from;
        for (uint i = 0; i < count; i++, sequence = Next(sequence))
        {
            if (i >= MaxRepublishPerGap)
            {
                lost += count - i;
                break;
            }
            // The server lists what it still has; an empty list from a server that does not fill it
            // in is not trusted, so then every number is tried.
            var message = listed.Count == 0 || listed.Contains(sequence) ? await RepublishAsync(s, sequence).ConfigureAwait(false) : null;
            if (message is null)
            {
                lost++;
                continue;
            }
            await DeliverAsync(s, message).ConfigureAwait(false);
        }
        s.LastSequenceNumber = Previous(until);
        if (lost > 0)
        {
            s.AddLost(lost);
            OpcUaTelemetry.LostNotificationMessages.Add(lost);
            LogMessagesLost(_logger, s.Id, lost);
            s.Raise(new SubscriptionEvent(SubscriptionEventKind.NotificationsLost, StatusCodes.BadMessageNotAvailable, lost));
        }
    }

    private async Task<NotificationMessage?> RepublishAsync(OpcUaSubscription s, uint sequence)
    {
        try
        {
            var request = new RepublishRequest(_host.Header(), s.Id, sequence).Encode();
            var bytes = await _host.SendAsync("Republish", SubscriptionEncodingIds.RepublishRequest, SubscriptionEncodingIds.RepublishResponse,
                request, null, _stop.Token).ConfigureAwait(false);
            var response = ServiceCodec.Decode(bytes, RepublishResponse.Decode);
            if (response.Header.ServiceResult.IsBad)
            {
                return null; // BadMessageNotAvailable: the server dropped it already
            }
            OpcUaTelemetry.Republished.Add(1);
            return response.NotificationMessage;
        }
        catch (OpcUaException ex)
        {
            LogRepublishFailed(_logger, s.Id, sequence, ex.StatusCode.Name);
            return null;
        }
    }

    private async Task DeliverAsync(OpcUaSubscription s, NotificationMessage message)
    {
        var recreate = false;
        foreach (var data in message.NotificationData)
        {
            if (data.Encoding != ExtensionObjectEncoding.Binary)
            {
                continue;
            }
            if (data.TypeId == DataChangeType)
            {
                var changes = ServiceCodec.Decode(data.BinaryBody.ToArray(), DataChangeNotificationMessage.Decode);
                foreach (var change in changes.MonitoredItems)
                {
                    if (!s.TryGetItem(change.ClientHandle, out var item))
                    {
                        continue; // removed meanwhile
                    }
                    if (!await WriteAsync(s, new DataChange(item, change.Value, message.SequenceNumber, message.PublishTime)).ConfigureAwait(false))
                    {
                        return; // subscription disposed
                    }
                }
                OpcUaTelemetry.Notifications.Add(changes.MonitoredItems.Length);
            }
            else if (data.TypeId == StatusChangeType)
            {
                var status = ServiceCodec.Decode(data.BinaryBody.ToArray(), StatusChangeNotificationMessage.Decode).Status;
                LogServerStatusChange(_logger, s.Id, status.Name);
                s.Raise(new SubscriptionEvent(SubscriptionEventKind.ServerStatusChange, status));
                // BadTimeout: the server deleted the subscription because its lifetime ran out.
                recreate |= status == StatusCodes.BadTimeout;
            }
            // EventNotificationList (alarms and events) is not supported yet and ignored.
        }
        s.LastSequenceNumber = message.SequenceNumber;
        s.PendingAcks.Add(message.SequenceNumber);
        s.LastActivity = Environment.TickCount64; // delivery may have waited for a slow reader
        if (recreate)
        {
            await RecreateAsync(s).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> WriteAsync(OpcUaSubscription s, DataChange change)
    {
        if (s.Writer.TryWrite(change))
        {
            return true;
        }
        // Buffer full: wait for the reader. No new Publish is sent meanwhile, so the server keeps
        // queuing instead of the client dropping values.
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(s.Closing, _stop.Token);
            await s.Writer.WriteAsync(change, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or ChannelClosedException)
        {
            return false;
        }
    }

    // ---- keep-alive watchdog and recovery ------------------------------------------------------

    private async Task OnTickAsync()
    {
        if (!_sessionUp)
        {
            return;
        }
        var now = Environment.TickCount64;
        if (now >= _nextRecoveryAttempt && _subscriptions.Any(s => s.Generation != _generation))
        {
            await RecoverAsync().ConfigureAwait(false); // an earlier attempt failed: try again
            return;
        }
        var missed = false;
        foreach (var s in Active())
        {
            // A message is due every keep-alive period at the latest. Allow half a period more and
            // a few seconds for the network before treating the silence as a dead connection.
            var limit = (long)(s.KeepAlivePeriod.TotalMilliseconds * 1.5) + 3000;
            if (now - s.LastActivity > limit && !s.KeepAliveMissReported)
            {
                s.KeepAliveMissReported = true;
                missed = true;
                OpcUaTelemetry.KeepAliveMisses.Add(1);
                LogKeepAliveMissed(_logger, s.Id, now - s.LastActivity);
                s.Raise(new SubscriptionEvent(SubscriptionEventKind.KeepAliveMissed, StatusCodes.BadTimeout));
            }
        }
        if (missed)
        {
            _host.ReportKeepAliveMissed();
        }
    }

    /// <summary>
    /// Moves subscriptions of older sessions into the current one: TransferSubscriptions first
    /// (keeps the server-side queue, so Republish can fill the outage), recreate when that fails.
    /// </summary>
    private async Task RecoverAsync()
    {
        var stale = _subscriptions.Where(s => s.Generation != _generation && !s.Closing.IsCancellationRequested).ToList();
        if (stale.Count == 0)
        {
            return;
        }
        _nextRecoveryAttempt = Environment.TickCount64 + (long)RecoveryRetryInterval.TotalMilliseconds;

        TransferResult[]? results = null;
        var withId = stale.Where(s => s.Id != 0).ToList();
        if (withId.Count > 0)
        {
            try
            {
                var request = new TransferSubscriptionsRequest(_host.Header(), withId.Select(s => s.Id).ToArray(), true).Encode();
                var bytes = await _host.SendAsync("TransferSubscriptions", SubscriptionEncodingIds.TransferSubscriptionsRequest,
                    SubscriptionEncodingIds.TransferSubscriptionsResponse, request, null, _stop.Token).ConfigureAwait(false);
                var response = ServiceCodec.Decode(bytes, TransferSubscriptionsResponse.Decode);
                if (response.Header.ServiceResult.IsGood && response.Results.Length == withId.Count)
                {
                    results = response.Results;
                }
                else
                {
                    LogTransferRefused(_logger, response.Header.ServiceResult.Name);
                }
            }
            catch (OpcUaException ex)
            {
                LogTransferRefused(_logger, ex.StatusCode.Name);
            }
        }

        foreach (var s in stale)
        {
            var index = withId.IndexOf(s);
            var result = results is not null && index >= 0 ? results[index] : null;
            if (result is { Status.IsGood: true })
            {
                s.Generation = _generation;
                s.LastActivity = Environment.TickCount64;
                s.KeepAliveMissReported = false;
                _byId[s.Id] = s;
                OpcUaTelemetry.SubscriptionRecoveries.Add(1, new KeyValuePair<string, object?>("kind", "transferred"));
                LogTransferred(_logger, s.Id, result.AvailableSequenceNumbers.Length);
                s.Raise(new SubscriptionEvent(SubscriptionEventKind.Transferred, StatusCodes.Good));
                // Fetch what was sent while we were away, in order, up to the newest number the server still has.
                var newer = result.AvailableSequenceNumbers.Where(n => !IsAtOrBefore(n, s.LastSequenceNumber)).ToArray();
                if (newer.Length > 0)
                {
                    var last = newer.MaxBy(n => Distance(s.LastSequenceNumber, n));
                    await FillGapAsync(s, Next(s.LastSequenceNumber), Next(last), newer).ConfigureAwait(false);
                }
            }
            else
            {
                if (result is not null)
                {
                    LogTransferItemRefused(_logger, s.Id, result.Status.Name);
                }
                await RecreateAsync(s).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Creates the subscription and all its items again in the current session.</summary>
    private async Task RecreateAsync(OpcUaSubscription s)
    {
        if (s.Closing.IsCancellationRequested)
        {
            return;
        }
        try
        {
            await s.Gate.WaitAsync(s.Closing).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        try
        {
            if (_byId.TryGetValue(s.Id, out var current) && current == s)
            {
                _byId.Remove(s.Id);
            }
            await s.CreateOnServerAsync(_stop.Token).ConfigureAwait(false);
            await s.RecreateItemsAsync(_stop.Token).ConfigureAwait(false);
            s.Generation = _generation;
            _byId[s.Id] = s;
            OpcUaTelemetry.SubscriptionRecoveries.Add(1, new KeyValuePair<string, object?>("kind", "recreated"));
            LogRecreated(_logger, s.Id, s.Items.Count);
            s.Raise(new SubscriptionEvent(SubscriptionEventKind.Recreated, StatusCodes.Good));
        }
        catch (OpcUaException ex)
        {
            s.Generation = -1; // stale: retried on the next tick or session
            LogRecoveryFailed(_logger, ex.StatusCode.Name);
            s.Raise(new SubscriptionEvent(SubscriptionEventKind.RecoveryFailed, ex.StatusCode));
        }
        finally
        {
            s.Gate.Release();
        }
    }

    // ---- sequence number arithmetic (wraps from 4294967295 to 1; 0 is never used) --------------

    internal static uint Next(uint sequence) => sequence == uint.MaxValue ? 1 : sequence + 1;

    internal static uint Previous(uint sequence) => sequence <= 1 ? uint.MaxValue : sequence - 1;

    /// <summary>Steps forward from <paramref name="from"/> to <paramref name="to"/>, skipping 0.</summary>
    internal static uint Distance(uint from, uint to) => to >= from ? to - from : uint.MaxValue - from + to;

    /// <summary>True when <paramref name="sequence"/> is <paramref name="reference"/> or less than half the number space behind it.</summary>
    internal static bool IsAtOrBefore(uint sequence, uint reference) => Distance(sequence, reference) < int.MaxValue;

    // ---- shutdown ---------------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        await _timer.DisposeAsync().ConfigureAwait(false);
        _inbox.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    // ---- messages -----------------------------------------------------------------------------

    private sealed record SessionUpMessage(long Generation);

    private sealed record SessionDownMessage
    {
        public static readonly SessionDownMessage Instance = new();
    }

    private sealed record Tick
    {
        public static readonly Tick Instance = new();
    }

    private sealed record AddMessage(OpcUaSubscription Subscription);

    private sealed record RemoveMessage(OpcUaSubscription Subscription, TaskCompletionSource Done);

    private sealed record PublishDone(long Generation, byte[]? Response, Exception? Error, (OpcUaSubscription Subscription, uint Sequence)[] Acks);

    // ---- logs ---------------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Error, Message = "Subscription engine failed handling {Message}")]
    private static partial void LogEngineError(ILogger logger, string message, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publish failed: {Status}")]
    private static partial void LogPublishFailed(ILogger logger, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Subscription {SubscriptionId}: {Count} notification messages could not be recovered")]
    private static partial void LogMessagesLost(ILogger logger, uint subscriptionId, long count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Subscription {SubscriptionId}: Republish of {Sequence} failed: {Status}")]
    private static partial void LogRepublishFailed(ILogger logger, uint subscriptionId, uint sequence, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Subscription {SubscriptionId}: server reports {Status}")]
    private static partial void LogServerStatusChange(ILogger logger, uint subscriptionId, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Subscription {SubscriptionId}: no keep-alive for {ElapsedMs} ms")]
    private static partial void LogKeepAliveMissed(ILogger logger, uint subscriptionId, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "TransferSubscriptions not possible ({Status}); subscriptions will be recreated")]
    private static partial void LogTransferRefused(ILogger logger, string status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription {SubscriptionId} could not be transferred: {Status}")]
    private static partial void LogTransferItemRefused(ILogger logger, uint subscriptionId, string status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription {SubscriptionId} transferred to the new session, {Available} messages available for Republish")]
    private static partial void LogTransferred(ILogger logger, uint subscriptionId, int available);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription recreated as {SubscriptionId} with {ItemCount} items")]
    private static partial void LogRecreated(ILogger logger, uint subscriptionId, int itemCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recreating a subscription failed: {Status}; retrying later")]
    private static partial void LogRecoveryFailed(ILogger logger, string status);
}
