using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Cictna.Industrial.OpcUa.Internal;

/// <summary>
/// A dedicated thread that owns one native object (client or server) and makes every native call
/// for it.
/// </summary>
/// <remarks>
/// <para>
/// Why: open62541 objects are driven by an event loop that the application must run, and our
/// design rule is that all native calls for one object are serialized (docs/roadmap.md). Giving
/// each object its own thread makes that true by construction: callers on any thread post work
/// with <see cref="InvokeAsync{T}"/> and await the result; the worker runs it between event-loop
/// iterations. No locks are needed around native state, and a slow caller can never block the
/// event loop (keep-alives, timeouts and responses keep flowing).
/// </para>
/// <para>
/// Loop: run queued commands, then <see cref="Iterate"/> (which drives the native event loop and
/// collects results), repeat. <see cref="Iterate"/> must return within a few milliseconds.
/// </para>
/// </remarks>
internal abstract partial class NativeWorker : IDisposable
{
    private readonly ConcurrentQueue<WorkItem> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopping;
    private int _disposed;

    protected NativeWorker(string threadName, ILogger logger)
    {
        Logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = threadName };
    }

    protected ILogger Logger { get; }

    /// <summary>True on the worker thread itself.</summary>
    protected bool OnWorkerThread => Thread.CurrentThread == _thread;

    /// <summary>Completes when the worker thread has finished <see cref="OnStart"/>.</summary>
    protected Task StartAsync()
    {
        _thread.Start();
        return _started.Task;
    }

    /// <summary>Runs <paramref name="action"/> on the worker thread and returns its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stopping)
        {
            tcs.SetException(new ObjectDisposedException(GetType().Name));
            return tcs.Task;
        }
        Enqueue(new WorkItem(
            () =>
            {
                try
                {
                    tcs.SetResult(action());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            },
            ex => tcs.TrySetException(ex)));
        return tcs.Task;
    }

    private void Enqueue(WorkItem item)
    {
        _commands.Enqueue(item);
        Wake();
        // Lost race with shutdown: the loop has already drained the queue for the last time.
        // Fail whatever is left so no caller waits forever.
        if (_stopped.Task.IsCompleted)
        {
            FailRemaining();
        }
    }

    private void FailRemaining()
    {
        while (_commands.TryDequeue(out var item))
        {
            item.Fail(new ObjectDisposedException(GetType().Name));
        }
    }

    private readonly record struct WorkItem(Action Run, Action<Exception> Fail);

    /// <summary>Runs <paramref name="action"/> on the worker thread without waiting for it.</summary>
    protected void Post(Action action)
    {
        if (_stopping)
        {
            return;
        }
        Enqueue(new WorkItem(action, _ => { }));
    }

    /// <summary>Creates the native object. Runs on the worker thread before the loop starts.</summary>
    protected abstract void OnStart();

    /// <summary>One event-loop step. Runs on the worker thread; must return within milliseconds.</summary>
    protected abstract void Iterate();

    /// <summary>Releases the native object. Runs on the worker thread after the loop ends.</summary>
    protected abstract void OnStop();

    /// <summary>Waits up to <paramref name="milliseconds"/> or until a command is posted.</summary>
    protected void WaitForWork(int milliseconds) => _wake.WaitOne(milliseconds);

    private void Run()
    {
        try
        {
            OnStart();
            _started.SetResult();
        }
        catch (Exception ex)
        {
            _stopping = true;
            _started.SetException(ex);
            _stopped.SetResult();
            return;
        }

        try
        {
            while (!_stopping)
            {
                while (_commands.TryDequeue(out var command))
                {
                    command.Run();
                }
                try
                {
                    Iterate();
                }
                catch (Exception ex)
                {
                    // A failing iteration must not kill the thread: log and keep the loop alive.
                    LogLoopError(Logger, ex);
                    WaitForWork(50);
                }
            }
        }
        finally
        {
            try
            {
                OnStop();
            }
            catch (Exception ex)
            {
                LogStopError(Logger, ex);
            }
            _stopped.SetResult();
            // Commands still queued fail with ObjectDisposedException instead of waiting forever.
            FailRemaining();
        }
    }

    /// <summary>Asks the loop to end, then waits for OnStop to finish. Safe to call repeatedly.</summary>
    protected Task StopAsync()
    {
        _stopping = true;
        Wake();
        return _stopped.Task;
    }

    private void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed: the loop has ended, nothing to wake.
        }
    }

    /// <summary>Stops the loop (if still running) and releases the wake-up event. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (!OnWorkerThread && _thread.ThreadState != ThreadState.Unstarted)
        {
            StopAsync().GetAwaiter().GetResult();
        }
        _wake.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in the OPC UA worker loop")]
    private static partial void LogLoopError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error while stopping the OPC UA worker")]
    private static partial void LogStopError(ILogger logger, Exception exception);
}
