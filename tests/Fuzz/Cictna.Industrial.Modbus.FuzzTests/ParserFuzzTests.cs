using System.Buffers.Binary;
using Cictna.Industrial.Modbus.Protocol;

namespace Cictna.Industrial.Modbus.FuzzTests;

/// <summary>
/// The two PDU parsers with hundreds of thousands of valid, mutated and random inputs: the
/// server's request handling and the client's response check. Neither may throw, and what they
/// accept must be safe to use.
/// </summary>
public sealed class ParserFuzzTests
{
    private static readonly byte[] Supported = [1, 2, 3, 4, 5, 6, 15, 16, 23];

    private static void Run(int baseline, Action<Mutator, int> body)
    {
        var seed = Mutator.Seed(out _);
        var mutator = new Mutator(seed);
        var iterations = Mutator.Iterations(baseline);
        for (var i = 0; i < iterations; i++)
        {
            try
            {
                body(mutator, i);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.IAssertionException)
            {
                throw new InvalidOperationException($"Crash at iteration {i}; replay with CICTNA_FUZZ_SEED={seed}", ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Invariant broken at iteration {i}; replay with CICTNA_FUZZ_SEED={seed}: {ex.Message}", ex);
            }
        }
    }

    [Fact]
    public void TheServerAnswersEveryRequestWithAWellFormedResponse()
    {
        var full = new ModbusDataStore();
        var small = new ModbusDataStore(coils: 100, discreteInputs: 50, holdingRegisters: 300, inputRegisters: 10);
        var response = new byte[ModbusPdu.MaxLength];
        Run(200_000, (m, i) =>
        {
            var valid = m.ValidRequest();
            var mutated = m.Random.Next(3) != 0;
            var request = mutated ? m.Mutate(valid) : valid;
            if (request.Length == 0)
            {
                return; // the framing guarantees at least a function code
            }
            var store = m.Random.Next(2) == 0 ? full : small;
            response.AsSpan().Fill(0xEE);
            var length = ModbusRequestHandler.Handle(store, request, response);

            Assert.InRange(length, 2, ModbusPdu.MaxLength);
            var function = request[0];
            if ((response[0] & 0x80) != 0)
            {
                Assert.Equal(2, length);
                Assert.Equal((byte)(function | 0x80), response[0]);
                Assert.InRange(response[1], (byte)1, (byte)4);
                Assert.False(!mutated && store == full, $"A valid request was refused: {Convert.ToHexString(request)} -> {Convert.ToHexString(response, 0, 2)}");
                return;
            }
            Assert.Contains(function, Supported);
            Assert.Equal(function, response[0]);
            switch (function)
            {
                case 1 or 2:
                    Assert.Equal((Mutator.Quantity(request) + 7) / 8, response[1]);
                    Assert.Equal(2 + response[1], length);
                    break;
                case 3 or 4:
                    Assert.Equal(Mutator.Quantity(request) * 2, response[1]);
                    Assert.Equal(2 + response[1], length);
                    break;
                case 23:
                    Assert.Equal(Mutator.Quantity(request) * 2, response[1]);
                    break;
                default:
                    Assert.Equal(5, length);
                    Assert.True(request.AsSpan(0, 5).SequenceEqual(response.AsSpan(0, 5)));
                    break;
            }
        });
    }

    [Fact]
    public void TheClientNeverTrustsAResponseThatDoesNotFit()
    {
        var store = new ModbusDataStore();
        var correct = new byte[ModbusPdu.MaxLength];
        Run(300_000, (m, i) =>
        {
            var request = m.ValidRequest();
            var length = ModbusRequestHandler.Handle(store, request, correct);
            var expected = correct.AsSpan(0, length).ToArray();
            Assert.True(ModbusPdu.Check(request, expected).IsGood, "the real answer must be accepted");

            var response = m.Mutate(expected);
            var result = ModbusPdu.Check(request, response);
            if (!result.IsGood)
            {
                return;
            }
            // Accepted: the data must be exactly what the request asked for, and readable.
            switch (request[0])
            {
                case 1 or 2:
                    var bits = Mutator.Quantity(request);
                    Assert.Equal(2 + (bits + 7) / 8, response.Length);
                    Assert.Equal(bits, ModbusPdu.UnpackBits(ModbusPdu.Data(response), bits).Length);
                    break;
                case 3 or 4 or 23:
                    var registers = Mutator.Quantity(request);
                    Assert.Equal(2 + registers * 2, response.Length);
                    Assert.Equal(registers, ModbusPdu.ReadRegisters(ModbusPdu.Data(response), registers).Length);
                    break;
                case 5 or 6:
                    Assert.Equal(request, response);
                    break;
                default:
                    Assert.Equal(request.AsSpan(0, 5).ToArray(), response);
                    break;
            }
        });
    }

    [Fact]
    public void ExceptionResponsesAreOnlyAcceptedForTheirOwnFunction()
    {
        Run(100_000, (m, i) =>
        {
            var request = m.ValidRequest();
            var response = new[] { (byte)m.Random.Next(256), (byte)m.Random.Next(256) };
            var result = ModbusPdu.Check(request, response);
            if (result.Exception != ModbusExceptionCode.None)
            {
                Assert.Equal((byte)(request[0] | 0x80), response[0]);
                Assert.NotEqual(0, response[1]);
                Assert.True(result.Status.IsBad);
            }
        });
    }

    [Fact]
    public void FrameHeadersOfAnyBytesAreJudgedConsistently()
    {
        var header = new byte[MbapHeader.Size];
        Run(100_000, (m, i) =>
        {
            m.Random.NextBytes(header);
            var mbap = MbapHeader.Read(header);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
            Assert.Equal(length is >= 2 and <= 254, mbap.HasValidLength);
            if (mbap.HasValidLength)
            {
                Assert.InRange(mbap.PduLength, 1, ModbusPdu.MaxLength);
            }
        });
    }
}
