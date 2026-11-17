using MessagePack;

namespace Talewright.Runtime.Saves;

/// <summary>Opaque continuation data carried by a player save.</summary>
public sealed class SaveContinuation
{
    internal SaveContinuation(ContinuationPayload payload) => Payload = payload;

    internal ContinuationPayload Payload { get; }
}

/// <summary>Describes a load that continued from a checkpoint after a story update.</summary>
public sealed record ContinuationReport
{
    public bool RedoDiscarded { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class ContinuationPayload
{
    [Key(0)] public CheckpointPayload? Current { get; set; }
    [Key(1)] public List<FramePayload>? Transcript { get; set; }
    [Key(2)] public CheckpointPayload? Base { get; set; }
    [Key(3)] public int BaseTranscriptLength { get; set; }
}
