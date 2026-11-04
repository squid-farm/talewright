using MessagePack;

namespace Talewright.Runtime.Saves;

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class SavePayload
{
    [Key(0)]
    public string StoryId { get; set; } = "";

    [Key(1)]
    public string StoryVersion { get; set; } = "";

    [Key(2)]
    public string ContentFingerprint { get; set; } = "";

    [Key(3)]
    public ulong Seed { get; set; }

    [Key(4)]
    public int Cursor { get; set; }

    [Key(5)]
    public CharacterPayload? Character { get; set; }

    [Key(6)]
    public List<string> Choices { get; set; } = [];

    [Key(7)]
    public byte[]? Summary { get; set; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class CharacterPayload
{
    [Key(0)]
    public string? FirstName { get; set; }

    [Key(1)]
    public string? LastName { get; set; }

    [Key(2)]
    public string? Pronouns { get; set; }

    [Key(3)]
    public string? Preset { get; set; }

    [Key(4)]
    public List<StatPayload> Stats { get; set; } = [];
}

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class StatPayload
{
    [Key(0)]
    public string Id { get; set; } = "";

    [Key(1)]
    public int Value { get; set; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class SummaryPayload
{
    [Key(0)]
    public string SceneTitle { get; set; } = "";

    [Key(1)]
    public long SavedAtUtcTicks { get; set; }

    [Key(2)]
    public int OffsetMinutes { get; set; }

    [Key(3)]
    public string? CharacterName { get; set; }
}
