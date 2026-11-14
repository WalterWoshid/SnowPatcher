using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

internal sealed record FunctionChange(string Identity, string BodyHash, int Start, int End, bool Insertion, string Fingerprint);
internal sealed record FunctionPreview(byte[] Bytes, IReadOnlyList<FunctionChange> Changes);
internal sealed partial class SslModule
{
    private static readonly HashSet<int> Branches = [22, 24, 25, 26];
    // Unrecognized instructions or metadata may carry relocatable offsets. Refuse them rather than guessing.
    private static readonly HashSet<int> Opcodes = [1, 2, 3, 4, 5, 6, 7, 8, 10, 13, 15, 18, 19, 20, 21, 22, 24, 25, 26, 27, 32, 36, 48, 49, 50, 53, 56, 57, 58, 59, 60, 63, 65, 66, 68, 69, 72];
    private readonly byte[] _bytes;
    private readonly List<Field> _fields;
    private readonly string[] _strings;
    private readonly Field[] _bodies;
    public SslModule(byte[] bytes)
    {
        ValidateHeader(bytes);
        _bytes = bytes;
        _fields = Fields(bytes[16..]);
        if (_fields.Any(f => f.Number is 6 or 7 or 8 && f.Wire != 2))
        {
            throw new CheckFailure("unsupported", "Unsupported SSLB shared-table or function layout.");
        }

        _strings = _fields.Where(f => f.Number == 6 && f.Wire == 2).Select(f => new UTF8Encoding(false, true).GetString(f.Data)).ToArray();
        _bodies = _fields.Where(f => f.Number == 8 && f.Wire == 2).ToArray();
    }

    public static void ValidateHeader(byte[] bytes)
    {
        if (bytes.Length < 16 || !bytes.AsSpan(0, 4).SequenceEqual("SSLB"u8) || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) != bytes.Length - 16)
        {
            throw new CheckFailure("unsupported", "Unsupported SSLB header or payload length.");
        }
        // The identity at offset 8 is opaque. Stock modules do not consistently use a name CRC.
        // Bundle membership resolves the name; identity uniqueness is checked separately.
    }

    internal static bool UsesConstantOperand(int op) => op is 6 or 15 or 20 or 53 or 57 or 58;
    private static int Int32(JsonElement value) => checked((int)PackageManifestReader.Integer(value));
    private string StringAt(ulong index) => index < (ulong)_strings.Length ? _strings[(int)index] : throw new CheckFailure("unsupported", "Invalid SSLB string reference.");
    private sealed record Field(int Number, int Wire, ulong Integer, byte[] Data, byte[] Encoded);
    private static List<Field> Fields(byte[] data)
    {
        var result = new List<Field>();
        var offset = 0;
        while (offset < data.Length)
        {
            var start = offset;
            var key = Varint(data, ref offset);
            var number = key >> 3;
            var wire = (int)(key & 7);
            if (number is 0 or > 536870911 || result.Count >= 100000)
            {
                throw new InvalidDataException("Invalid SSLB protobuf field.");
            }

            ulong integer = 0;
            byte[] value = [];
            if (wire == 0)
            {
                integer = Varint(data, ref offset);
            }
            else
            {
                var length = wire switch
                {
                    1 => 8UL,
                    5 => 4UL,
                    2 => Varint(data, ref offset),
                    _ => throw new CheckFailure("unsupported", "Unsupported SSLB protobuf wire type.")
                };
                if (length > (ulong)(data.Length - offset))
                {
                    throw new InvalidDataException("Truncated SSLB field.");
                }

                value = data.AsSpan(offset, (int)length).ToArray();
                offset += (int)length;
            }

            result.Add(new Field((int)number, wire, integer, value, data.AsSpan(start, offset - start).ToArray()));
        }

        return result;
    }

    private static Field One(IEnumerable<Field> fields, int number, int wire)
    {
        var found = fields.Where(f => f.Number == number).ToArray();
        if (found.Length != 1 || found[0].Wire != wire)
        {
            throw new CheckFailure("unsupported", "Unsupported or ambiguous SSLB field layout.");
        }

        return found[0];
    }

    private static ulong Varint(byte[] data, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (position >= data.Length)
            {
                throw new InvalidDataException("Truncated SSLB varint.");
            }

            var part = data[position++];
            if (shift == 63 && part > 1)
            {
                throw new InvalidDataException("Oversized SSLB varint.");
            }

            value |= (ulong)(part & 127) << shift;
            if (part < 128)
            {
                return value;
            }
        }

        throw new InvalidDataException("Oversized SSLB varint.");
    }

    private static byte[] Replace(IEnumerable<Field> fields, Field replaced, byte[] value)
    {
        using var output = new MemoryStream();
        foreach (var field in fields)
        {
            if (!ReferenceEquals(field, replaced))
            {
                output.Write(field.Encoded);
            }
            else
            {
                WriteVarint(output, ((ulong)field.Number << 3) | 2);
                WriteVarint(output, (ulong)value.Length);
                output.Write(value);
            }
        }

        return output.ToArray();
    }

    private static void WriteVarint(Stream output, ulong value)
    {
        while (value >= 128)
        {
            output.WriteByte((byte)(value | 128));
            value >>= 7;
        }

        output.WriteByte((byte)value);
    }
}
