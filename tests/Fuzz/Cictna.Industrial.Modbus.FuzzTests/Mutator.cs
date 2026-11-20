using System.Buffers.Binary;
using Cictna.Industrial.Modbus.Protocol;

namespace Cictna.Industrial.Modbus.FuzzTests;

/// <summary>
/// Seeded input generator: valid requests for every function code, and mutations that keep
/// inputs close to valid (flipped bits, boundary values in the count and length fields,
/// truncation, extension) next to plain random bytes.
/// </summary>
internal sealed class Mutator(int seed)
{
    private static readonly ushort[] Interesting = [0, 1, 2, 7, 8, 9, 0x7B, 0x7C, 0x7D, 0x7E, 0xF6, 0xF7, 0xFF, 0x100, 0x7B0, 0x7B1, 0x7D0, 0x7D1, 0x7FFF, 0x8000, 0xFFFE, 0xFFFF];

    public Random Random { get; } = new(seed);

    /// <summary>The seed of this run, for the failure message.</summary>
    public static int Seed(out bool fromEnvironment)
    {
        var text = Environment.GetEnvironmentVariable("CICTNA_FUZZ_SEED");
        fromEnvironment = int.TryParse(text, out var seed);
        return fromEnvironment ? seed : Random.Shared.Next();
    }

    /// <summary>Iterations scaled by CICTNA_FUZZ_SCALE.</summary>
    public static int Iterations(int baseline) =>
        int.TryParse(Environment.GetEnvironmentVariable("CICTNA_FUZZ_SCALE"), out var scale) && scale > 0 ? baseline * scale : baseline;

    /// <summary>A valid request for a random supported function code, as our client builds them.</summary>
    public byte[] ValidRequest()
    {
        var address = (ushort)Random.Next(0, 65536);
        switch (Random.Next(9))
        {
            case 0: return ModbusPdu.Read(ModbusFunctionCode.ReadCoils, Fit(address, 2000, out var q1), q1);
            case 1: return ModbusPdu.Read(ModbusFunctionCode.ReadDiscreteInputs, Fit(address, 2000, out var q2), q2);
            case 2: return ModbusPdu.Read(ModbusFunctionCode.ReadHoldingRegisters, Fit(address, 125, out var q3), q3);
            case 3: return ModbusPdu.Read(ModbusFunctionCode.ReadInputRegisters, Fit(address, 125, out var q4), q4);
            case 4: return ModbusPdu.WriteSingleCoil(address, Random.Next(2) == 0);
            case 5: return ModbusPdu.WriteSingleRegister(address, (ushort)Random.Next(65536));
            case 6:
                var coils = new bool[Random.Next(1, 1969)];
                for (var i = 0; i < coils.Length; i++)
                {
                    coils[i] = Random.Next(2) == 0;
                }
                return ModbusPdu.WriteMultipleCoils(Fit(address, coils.Length), coils);
            case 7:
                return ModbusPdu.WriteMultipleRegisters(Fit(address, Random.Next(1, 124), out var n), Registers(n));
            default:
                var read = Random.Next(1, 126);
                var write = Random.Next(1, 122);
                return ModbusPdu.ReadWriteMultipleRegisters(Fit(address, read), read, Fit((ushort)Random.Next(65536), write), Registers(write));
        }
    }

    /// <summary>A copy of <paramref name="input"/> with one to four mutations, or random bytes.</summary>
    public byte[] Mutate(byte[] input)
    {
        var data = new List<byte>(input);
        var rounds = Random.Next(1, 5);
        for (var r = 0; r < rounds; r++)
        {
            switch (Random.Next(9))
            {
                case 0 when data.Count > 0: // flip a bit
                    var i = Random.Next(data.Count);
                    data[i] ^= (byte)(1 << Random.Next(8));
                    break;
                case 1 when data.Count > 0: // random byte
                    data[Random.Next(data.Count)] = (byte)Random.Next(256);
                    break;
                case 2 when data.Count > 2: // boundary value in a 16-bit field
                    var at = Random.Next(data.Count - 1);
                    var value = Interesting[Random.Next(Interesting.Length)];
                    data[at] = (byte)(value >> 8);
                    data[at + 1] = (byte)value;
                    break;
                case 3 when data.Count > 0: // truncate
                    var keep = Random.Next(data.Count);
                    data.RemoveRange(keep, data.Count - keep);
                    break;
                case 4: // extend
                    for (var n = Random.Next(1, 8); n > 0; n--)
                    {
                        data.Add((byte)Random.Next(256));
                    }
                    break;
                case 5 when data.Count > 0: // delete one byte
                    data.RemoveAt(Random.Next(data.Count));
                    break;
                case 6: // insert one byte
                    data.Insert(Random.Next(data.Count + 1), (byte)Random.Next(256));
                    break;
                case 7 when data.Count > 0: // any function code, including exception codes
                    data[0] = (byte)Random.Next(256);
                    break;
                default: // plain random bytes
                    data.Clear();
                    for (var n = Random.Next(0, 300); n > 0; n--)
                    {
                        data.Add((byte)Random.Next(256));
                    }
                    break;
            }
        }
        return [.. data];
    }

    private ushort[] Registers(int count)
    {
        var values = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = (ushort)Random.Next(65536);
        }
        return values;
    }

    private static ushort Fit(ushort address, int quantity) => (ushort)Math.Min(address, 65536 - quantity);

    private ushort Fit(ushort address, int max, out int quantity)
    {
        quantity = Random.Next(1, max + 1);
        return Fit(address, quantity);
    }

    /// <summary>Reads the quantity field (bytes 3 and 4) of a request.</summary>
    public static int Quantity(byte[] request) => BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(3));
}
