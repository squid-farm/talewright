using System.Buffers;
using System.Text;

namespace Talewright.Runtime.Saves;

/// <summary>
/// Minimal writer for the protobuf wire format (https://protobuf.dev/programming-guides/encoding/),
/// written from the published specification. See docs/developer/save-format.md.
/// </summary>
internal sealed class ProtoWriter
{
    public const int Varint = 0;
    public const int Fixed64 = 1;
    public const int LengthDelimited = 2;
    public const int Fixed32 = 5;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;

    public void WriteTag(int field, int wireType) => WriteVarint(((ulong)(uint)field << 3) | (uint)wireType);

    public void WriteVarint(ulong value)
    {
        var span = _buffer.GetSpan(10);
        var length = 0;
        while (value >= 0x80)
        {
            span[length++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[length++] = (byte)value;
        _buffer.Advance(length);
    }

    public void WriteUInt64(int field, ulong value)
    {
        WriteTag(field, Varint);
        WriteVarint(value);
    }

    public void WriteSInt32(int field, int value)
    {
        WriteTag(field, Varint);
        WriteVarint((uint)((value << 1) ^ (value >> 31)));
    }

    public void WriteSInt64(int field, long value)
    {
        WriteTag(field, Varint);
        WriteVarint((ulong)((value << 1) ^ (value >> 63)));
    }

    public void WriteFixed64(int field, ulong value)
    {
        WriteTag(field, Fixed64);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(_buffer.GetSpan(8), value);
        _buffer.Advance(8);
    }

    public void WriteString(int field, string value)
    {
        // Like Google's writers, unpaired surrogates become U+FFFD rather than failing the save.
        WriteTag(field, LengthDelimited);
        var length = Utf8.GetByteCount(value);
        WriteVarint((uint)length);
        Utf8.GetBytes(value, _buffer.GetSpan(length));
        _buffer.Advance(length);
    }

    public void WriteBytes(int field, ReadOnlySpan<byte> value)
    {
        WriteTag(field, LengthDelimited);
        WriteVarint((uint)value.Length);
        _buffer.Write(value);
    }

    public void WriteMessage(int field, Action<ProtoWriter> write)
    {
        var nested = new ProtoWriter();
        write(nested);
        WriteBytes(field, nested.WrittenSpan);
    }

    public void WritePackedUInt32(int field, IReadOnlyList<uint> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        var packed = new ProtoWriter();
        foreach (var value in values)
        {
            packed.WriteVarint(value);
        }

        WriteBytes(field, packed.WrittenSpan);
    }
}

/// <summary>Bounds-checked reader for the protobuf wire format. Every failure is <see cref="InvalidDataException"/>.</summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly bool End => _position >= _data.Length;
    public readonly int Position => _position;

    public (int Field, int WireType) ReadTag()
    {
        var tag = ReadVarint();
        var field = tag >> 3;
        // Field numbers are 1 to 2^29-1; 19000-19999 are reserved by protobuf but still skippable.
        if (field is 0 or > (1u << 29) - 1)
        {
            throw Malformed();
        }

        return ((int)field, (int)(tag & 7));
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        for (var i = 0; i < 10; i++)
        {
            if (_position >= _data.Length)
            {
                throw Malformed();
            }

            var b = _data[_position++];
            // The tenth byte may only carry bit 63; anything more overflows 64 bits.
            if (i == 9 && b > 1)
            {
                throw Malformed();
            }

            result |= (ulong)(b & 0x7F) << (7 * i);
            if (b < 0x80)
            {
                return result;
            }
        }

        throw Malformed();
    }

    public uint ReadUInt32()
    {
        var value = ReadVarint();
        return value <= uint.MaxValue ? (uint)value : throw Malformed();
    }

    public int ReadSInt32()
    {
        var value = ReadUInt32();
        return (int)(value >> 1) ^ -(int)(value & 1);
    }

    public long ReadSInt64()
    {
        var value = ReadVarint();
        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    public ulong ReadFixed64() => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = ReadVarint();
        return length <= (ulong)(_data.Length - _position) ? Take((int)length) : throw Malformed();
    }

    public string ReadString()
    {
        var bytes = ReadBytes();
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(SaveFormat.MalformedMessage, ex);
        }
    }

    /// <summary>Skips an unknown field. Groups (wire types 3 and 4) and undefined wire types are rejected.</summary>
    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case ProtoWriter.Varint:
                ReadVarint();
                break;
            case ProtoWriter.Fixed64:
                Take(8);
                break;
            case ProtoWriter.LengthDelimited:
                ReadBytes();
                break;
            case ProtoWriter.Fixed32:
                Take(4);
                break;
            default:
                throw Malformed();
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > _data.Length - _position)
        {
            throw Malformed();
        }

        var span = _data.Slice(_position, count);
        _position += count;
        return span;
    }

    private static InvalidDataException Malformed() => new(SaveFormat.MalformedMessage);
}
