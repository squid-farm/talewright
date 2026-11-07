using System.Buffers.Binary;
using System.Text;

namespace Talewright.Runtime.Saves;

public sealed record TriggerSave(string TriggerId, byte[] Data)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(TriggerId) || TriggerId.Any(char.IsControl)
            || Encoding.UTF8.GetByteCount(TriggerId) > SaveFormat.MaxSaveBytes)
        {
            throw new ArgumentException("The trigger checkpoint has an invalid trigger identifier.", nameof(TriggerId));
        }

        ArgumentNullException.ThrowIfNull(Data);
        SaveNames.ValidateData(Data);
    }
}

internal static class TriggerSaveCodec
{
    private const byte Version = 1;
    private const int HeaderLength = 4 + 1 + sizeof(uint) + sizeof(uint);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static ReadOnlySpan<byte> Magic => "TWTC"u8;
    internal const int MaxEncodedBytes = 2 * SaveFormat.MaxSaveBytes + HeaderLength;

    internal static byte[] Encode(TriggerSave save)
    {
        save.Validate();
        var triggerId = StrictUtf8.GetBytes(save.TriggerId);
        var encoded = new byte[checked(HeaderLength + triggerId.Length + save.Data.Length)];
        Magic.CopyTo(encoded);
        encoded[4] = Version;
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(5), checked((uint)triggerId.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(9), checked((uint)save.Data.Length));
        triggerId.CopyTo(encoded.AsSpan(HeaderLength));
        save.Data.CopyTo(encoded.AsSpan(HeaderLength + triggerId.Length));
        return encoded;
    }

    internal static TriggerSave Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < HeaderLength || encoded.Length > MaxEncodedBytes
            || !encoded[..Magic.Length].SequenceEqual(Magic) || encoded[4] != Version)
        {
            throw new InvalidDataException("The trigger checkpoint is malformed or damaged.");
        }

        var triggerIdLength = BinaryPrimitives.ReadUInt32LittleEndian(encoded[5..]);
        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(encoded[9..]);
        if (triggerIdLength is 0 or > SaveFormat.MaxSaveBytes || dataLength is 0 or > SaveFormat.MaxSaveBytes
            || (ulong)HeaderLength + triggerIdLength + dataLength != (ulong)encoded.Length)
        {
            throw new InvalidDataException("The trigger checkpoint is malformed or damaged.");
        }

        try
        {
            var triggerId = StrictUtf8.GetString(encoded.Slice(HeaderLength, checked((int)triggerIdLength)));
            var data = encoded[(HeaderLength + checked((int)triggerIdLength))..].ToArray();
            var save = new TriggerSave(triggerId, data);
            save.Validate();
            return save;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            throw new InvalidDataException("The trigger checkpoint is malformed or damaged.", ex);
        }
    }
}
