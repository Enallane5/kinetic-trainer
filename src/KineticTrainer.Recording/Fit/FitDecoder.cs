using System.Buffers.Binary;
using System.Text;

namespace KineticTrainer.Recording.Fit;

/// <summary>A decoded data message: global message number and raw field values keyed by field definition number.</summary>
public sealed record FitMessage(ushort GlobalNumber, IReadOnlyDictionary<byte, object> Fields)
{
    public long? Int(byte field) => Fields.TryGetValue(field, out var v) ? v switch
    {
        byte b => b,
        ushort u => u,
        uint u => u,
        sbyte s => s,
        short s => s,
        int i => i,
        _ => null,
    } : null;

    public string? Text(byte field) => Fields.TryGetValue(field, out var v) && v is string s ? s : null;
}

/// <summary>
/// Minimal FIT reader for verification: checks header and CRCs, tracks local definitions, decodes the base types
/// this app writes (plus compressed-timestamp headers are rejected, since we never produce them). Invalid values
/// (0xFF / 0xFFFF / 0xFFFFFFFF) are omitted from <see cref="FitMessage.Fields"/>.
/// </summary>
public static class FitDecoder
{
    public sealed record FitFile(byte HeaderSize, byte ProtocolVersion, ushort ProfileVersion, uint DataSize, bool HeaderCrcValid, bool FileCrcValid, IReadOnlyList<FitMessage> Messages);

    public static FitFile Decode(ReadOnlySpan<byte> file)
    {
        if (file.Length < 14)
        {
            throw new InvalidDataException("File shorter than a FIT header.");
        }
        var headerSize = file[0];
        if (headerSize is not (12 or 14))
        {
            throw new InvalidDataException($"Unexpected header size {headerSize}.");
        }
        if (Encoding.ASCII.GetString(file.Slice(8, 4)) != ".FIT")
        {
            throw new InvalidDataException("Missing .FIT signature.");
        }
        var protocol = file[1];
        var profile = BinaryPrimitives.ReadUInt16LittleEndian(file[2..]);
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        var headerCrcValid = headerSize < 14 || BinaryPrimitives.ReadUInt16LittleEndian(file[12..]) == FitCrc.Compute(file[..12]);
        if (file.Length < headerSize + dataSize + 2)
        {
            throw new InvalidDataException("File truncated.");
        }
        var fileCrcValid = BinaryPrimitives.ReadUInt16LittleEndian(file[(int)(headerSize + dataSize)..]) == FitCrc.Compute(file[..(int)(headerSize + dataSize)]);

        var messages = new List<FitMessage>();
        var definitions = new Dictionary<byte, (ushort Global, bool BigEndian, (byte Field, byte Size, byte BaseType)[] Fields)>();
        var pos = (int)headerSize;
        var end = (int)(headerSize + dataSize);
        while (pos < end)
        {
            var header = file[pos++];
            if ((header & 0x80) != 0)
            {
                throw new InvalidDataException("Compressed timestamp headers are not supported by this decoder.");
            }
            var local = (byte)(header & 0x0F);
            if ((header & 0x40) != 0)
            {
                var hasDeveloperData = (header & 0x20) != 0;
                pos++; // reserved
                var bigEndian = file[pos++] == 1;
                var global = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(file[pos..]) : BinaryPrimitives.ReadUInt16LittleEndian(file[pos..]);
                pos += 2;
                var count = file[pos++];
                var fields = new (byte, byte, byte)[count];
                for (var i = 0; i < count; i++)
                {
                    fields[i] = (file[pos], file[pos + 1], file[pos + 2]);
                    pos += 3;
                }
                if (hasDeveloperData)
                {
                    var devCount = file[pos++];
                    pos += devCount * 3;
                }
                definitions[local] = (global, bigEndian, fields);
            }
            else
            {
                if (!definitions.TryGetValue(local, out var def))
                {
                    throw new InvalidDataException($"Data message for undefined local type {local} at offset {pos - 1}.");
                }
                var values = new Dictionary<byte, object>();
                foreach (var (field, size, baseType) in def.Fields)
                {
                    var slice = file.Slice(pos, size);
                    pos += size;
                    var value = Read(slice, baseType, def.BigEndian);
                    if (value is not null)
                    {
                        values[field] = value;
                    }
                }
                messages.Add(new FitMessage(def.Global, values));
            }
        }
        return new FitFile(headerSize, protocol, profile, dataSize, headerCrcValid, fileCrcValid, messages);
    }

    private static object? Read(ReadOnlySpan<byte> b, byte baseType, bool bigEndian)
    {
        switch (baseType)
        {
            case FitProfile.BtEnum:
            case FitProfile.BtUInt8:
                return b[0] == 0xFF ? null : b[0];
            case FitProfile.BtUInt8z:
                return b[0] == 0 ? null : b[0];
            case FitProfile.BtSInt8:
                return b[0] == 0x7F ? null : (sbyte)b[0];
            case FitProfile.BtUInt16:
            case FitProfile.BtUInt16z:
            {
                var v = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b);
                return (baseType == FitProfile.BtUInt16 && v == 0xFFFF) || (baseType == FitProfile.BtUInt16z && v == 0) ? null : v;
            }
            case FitProfile.BtSInt16:
            {
                var v = bigEndian ? BinaryPrimitives.ReadInt16BigEndian(b) : BinaryPrimitives.ReadInt16LittleEndian(b);
                return v == 0x7FFF ? null : v;
            }
            case FitProfile.BtUInt32:
            case FitProfile.BtUInt32z:
            {
                var v = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
                return (baseType == FitProfile.BtUInt32 && v == 0xFFFFFFFF) || (baseType == FitProfile.BtUInt32z && v == 0) ? null : v;
            }
            case FitProfile.BtSInt32:
            {
                var v = bigEndian ? BinaryPrimitives.ReadInt32BigEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b);
                return v == 0x7FFFFFFF ? null : v;
            }
            case FitProfile.BtString:
            {
                var end = b.IndexOf((byte)0);
                var s = Encoding.UTF8.GetString(end < 0 ? b : b[..end]);
                return s.Length == 0 ? null : s;
            }
            default:
                return null; // types we never write
        }
    }
}
