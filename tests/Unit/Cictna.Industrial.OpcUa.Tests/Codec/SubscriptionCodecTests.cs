using Cictna.Industrial.Core;
using Cictna.Industrial.OpcUa.Encoding;
using Ids = Cictna.Industrial.OpcUa.Encoding.SubscriptionEncodingIds;

namespace Cictna.Industrial.OpcUa.Tests.Codec;

/// <summary>
/// The subscription and monitored item messages against open62541, byte for byte.
/// </summary>
/// <remarks>
/// Requests: open62541 must decode exactly what we encode and re-encode the same bytes (it keeps
/// every header field as sent). Responses: we encode random responses, open62541 must produce the
/// same bytes, and our decoder must read back the same values. A field in the wrong order or of the
/// wrong width fails here even if our own encoder and decoder agree with each other.
/// </remarks>
public sealed class SubscriptionCodecTests
{
    private const int Cases = 300;

    [Fact]
    public void SubscriptionRequestsMatchOpen62541()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var r = new Random(seed);
            var header = new RequestHeader((uint)r.Next(), (uint)r.Next(4));

            Same(Ids.CreateSubscriptionRequest, new CreateSubscriptionRequest(header, r.NextDouble() * 10000, (uint)r.Next(), (uint)r.Next(),
                (uint)r.Next(), r.Next(2) == 0, (byte)r.Next(256)).Encode(), seed);
            Same(Ids.ModifySubscriptionRequest, new ModifySubscriptionRequest(header, (uint)r.Next(), r.NextDouble() * 10000, (uint)r.Next(),
                (uint)r.Next(), (uint)r.Next(), (byte)r.Next(256)).Encode(), seed);
            Same(Ids.SetPublishingModeRequest, new SetPublishingModeRequest(header, r.Next(2) == 0, Ids32(r)).Encode(), seed);
            Same(Ids.DeleteSubscriptionsRequest, new DeleteSubscriptionsRequest(header, Ids32(r)).Encode(), seed);
            Same(Ids.TransferSubscriptionsRequest, new TransferSubscriptionsRequest(header, Ids32(r), r.Next(2) == 0).Encode(), seed);
            Same(Ids.DeleteMonitoredItemsRequest, new DeleteMonitoredItemsRequest(header, (uint)r.Next(), Ids32(r)).Encode(), seed);
            Same(Ids.RepublishRequest, new RepublishRequest(header, (uint)r.Next(), (uint)r.Next()).Encode(), seed);
            Same(Ids.PublishRequest, new PublishRequest(header,
                Enumerable.Range(0, r.Next(5)).Select(_ => new SubscriptionAcknowledgement((uint)r.Next(), (uint)r.Next())).ToArray()).Encode(), seed);
        }
    }

    [Fact]
    public void CreateMonitoredItemsRequestsMatchOpen62541()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var r = new Random(seed);
            var random = new RandomValues(seed);
            var items = Enumerable.Range(0, r.Next(1, 6)).Select(i =>
            {
                var filter = (i % 3) switch
                {
                    0 => ExtensionObject.Empty,
                    1 => new DataChangeFilterMessage((DataChangeTrigger)r.Next(3), (DeadbandType)r.Next(3), r.NextDouble() * 100).ToExtensionObject(),
                    _ => new DataChangeFilterMessage(DataChangeTrigger.StatusValue, DeadbandType.None, 0).ToExtensionObject(),
                };
                return new MonitoredItemCreateRequest(
                    new ReadValueId(random.NodeId(), AttributeId.Value, i == 2 ? "1:3" : null),
                    (MonitoringMode)r.Next(3),
                    new MonitoringParametersMessage((uint)r.Next(), r.NextDouble() * 1000, filter, (uint)r.Next(100), r.Next(2) == 0));
            }).ToArray();
            var request = new CreateMonitoredItemsRequest(new RequestHeader(1000), (uint)r.Next(), (TimestampsToReturn)r.Next(4), items).Encode();
            Same(Ids.CreateMonitoredItemsRequest, request, seed);
        }
    }

    [Fact]
    public void DataChangeFilterMatchesOpen62541()
    {
        var filter = new DataChangeFilterMessage(DataChangeTrigger.StatusValueTimestamp, DeadbandType.Absolute, 0.5).ToExtensionObject();
        var body = filter.BinaryBody.ToArray();
        Assert.Equal(body, Open62541Reference.RoundTrip(Ids.DataChangeFilter, body));
    }

    [Fact]
    public void SubscriptionResponsesMatchOpen62541AndDecodeBack()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var r = new Random(seed);
            var random = new RandomValues(seed);
            var header = Header(random, seed);

            var create = new CreateSubscriptionResponse(header, (uint)r.Next(), r.NextDouble() * 1000, (uint)r.Next(), (uint)r.Next());
            Assert.Equal(create, Decode(Same(Ids.CreateSubscriptionResponse, create.Encode(), seed), CreateSubscriptionResponse.Decode) with { Header = header });

            var modify = new ModifySubscriptionResponse(header, r.NextDouble() * 1000, (uint)r.Next(), (uint)r.Next());
            Assert.Equal(modify, Decode(Same(Ids.ModifySubscriptionResponse, modify.Encode(), seed), ModifySubscriptionResponse.Decode) with { Header = header });

            var statuses = new StatusResultsResponse(header, Enumerable.Range(0, r.Next(4)).Select(_ => new StatusCode((uint)r.Next() & 0xFFFF0000)).ToArray(), []);
            foreach (var id in new[] { Ids.SetPublishingModeResponse, Ids.DeleteSubscriptionsResponse, Ids.DeleteMonitoredItemsResponse })
            {
                var back = Decode(Same(id, statuses.Encode(), seed), StatusResultsResponse.Decode);
                Assert.Equal(statuses.Results, back.Results);
            }

            var transfer = new TransferSubscriptionsResponse(header,
                Enumerable.Range(0, r.Next(4)).Select(_ => new TransferResult(new StatusCode((uint)r.Next(2) << 31), Ids32(r))).ToArray(), []);
            var transferBack = Decode(Same(Ids.TransferSubscriptionsResponse, transfer.Encode(), seed), TransferSubscriptionsResponse.Decode);
            Assert.Equal(transfer.Results.Length, transferBack.Results.Length);
            for (var i = 0; i < transfer.Results.Length; i++)
            {
                Assert.Equal(transfer.Results[i].Status, transferBack.Results[i].Status);
                Assert.Equal(transfer.Results[i].AvailableSequenceNumbers, transferBack.Results[i].AvailableSequenceNumbers);
            }

            var items = new CreateMonitoredItemsResponse(header,
                Enumerable.Range(0, r.Next(4)).Select(_ => new MonitoredItemCreateResult(StatusCodes.Good, (uint)r.Next(), r.NextDouble() * 100, (uint)r.Next(10), ExtensionObject.Empty)).ToArray(), []);
            var itemsBack = Decode(Same(Ids.CreateMonitoredItemsResponse, items.Encode(), seed), CreateMonitoredItemsResponse.Decode);
            Assert.Equal(items.Results, itemsBack.Results);
        }
    }

    [Fact]
    public void PublishAndRepublishResponsesMatchOpen62541AndDecodeBack()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var r = new Random(seed);
            var random = new RandomValues(seed);
            var dataChange = new DataChangeNotificationMessage(
                Enumerable.Range(0, r.Next(6)).Select(_ => new MonitoredItemNotificationMessage((uint)r.Next(), random.DataValue(0))).ToArray(), []);
            var statusChange = new StatusChangeNotificationMessage(StatusCodes.BadTimeout, null);
            var data = (seed % 3) switch
            {
                0 => Array.Empty<ExtensionObject>(), // keep-alive
                1 => [ExtensionObject.FromBinary(new NodeId(Ids.DataChangeNotification), dataChange.Encode())],
                _ => [ExtensionObject.FromBinary(new NodeId(Ids.DataChangeNotification), dataChange.Encode()),
                      ExtensionObject.FromBinary(new NodeId(Ids.StatusChangeNotification), statusChange.Encode())],
            };
            var message = new NotificationMessage((uint)r.Next(), random.DateTime(), data);
            var publish = new PublishResponse(Header(random, seed), (uint)r.Next(), Ids32(r), r.Next(2) == 0, message, [StatusCodes.Good], []);

            var back = Decode(Same(Ids.PublishResponse, publish.Encode(), seed), PublishResponse.Decode);
            Assert.Equal(publish.SubscriptionId, back.SubscriptionId);
            Assert.Equal(publish.AvailableSequenceNumbers, back.AvailableSequenceNumbers);
            Assert.Equal(publish.MoreNotifications, back.MoreNotifications);
            Assert.Equal(message.SequenceNumber, back.NotificationMessage.SequenceNumber);
            Assert.Equal(message.IsKeepAlive, back.NotificationMessage.IsKeepAlive);
            if (!message.IsKeepAlive)
            {
                var body = back.NotificationMessage.NotificationData[0].BinaryBody;
                Assert.Equal(dataChange.MonitoredItems, Decode(body.ToArray(), DataChangeNotificationMessage.Decode).MonitoredItems);
            }

            var republish = new RepublishResponse(Header(random, seed), message);
            Decode(Same(Ids.RepublishResponse, republish.Encode(), seed), RepublishResponse.Decode);
        }
    }

    [Fact]
    public void NotificationBodiesMatchOpen62541()
    {
        for (var seed = 0; seed < Cases; seed++)
        {
            var r = new Random(seed);
            var random = new RandomValues(seed);
            var dataChange = new DataChangeNotificationMessage(
                Enumerable.Range(0, r.Next(8)).Select(_ => new MonitoredItemNotificationMessage((uint)r.Next(), random.DataValue(1))).ToArray(), []);
            var back = Decode(Same(Ids.DataChangeNotification, dataChange.Encode(), seed), DataChangeNotificationMessage.Decode);
            Assert.Equal(dataChange.MonitoredItems, back.MonitoredItems);

            var status = new StatusChangeNotificationMessage(new StatusCode((uint)r.Next() & 0xFFFF0000), seed % 2 == 0 ? random.DiagnosticInfo(0) : null);
            Assert.Equal(status.Status, Decode(Same(Ids.StatusChangeNotification, status.Encode(), seed), StatusChangeNotificationMessage.Decode).Status);
        }
    }

    private static uint[] Ids32(Random r) => Enumerable.Range(0, r.Next(5)).Select(_ => (uint)r.Next()).ToArray();

    // DiagnosticInfo and the string table are not part of the comparisons above (records compare
    // arrays by reference), so the header is put back on the decoded record before comparing.
    private static ResponseHeader Header(RandomValues random, int seed) =>
        new(random.DateTime(), (uint)seed, new StatusCode((uint)seed << 16), null, []);

    private static byte[] Same(uint typeId, byte[] bytes, int seed)
    {
        var reference = Open62541Reference.RoundTrip(typeId, bytes, $"type {typeId}, seed {seed}");
        Assert.True(bytes.AsSpan().SequenceEqual(reference), $"type {typeId}, seed {seed}: open62541 re-encoded differently");
        return reference;
    }

    private static T Decode<T>(byte[] bytes, Func<BinaryDecoder, T> read) => DifferentialCodecTests.Decode(bytes, read);
}
