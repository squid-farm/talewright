using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Talewright.Core.Identity;
using Talewright.Rules.Checks;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Model;

internal static class ChoiceReplayIdentity
{
    internal const int CompactLength = 8;
    internal const int FullLength = 32;

    internal static void Assign(IEnumerable<SceneDefinition> scenes) =>
        AssignCore(scenes, hash: null);

    internal static void Assign(IEnumerable<SceneDefinition> scenes, Func<byte[], byte[]> hash) =>
        AssignCore(scenes, hash);

    private static void AssignCore(IEnumerable<SceneDefinition> scenes, Func<byte[], byte[]>? hash)
    {
        var allScenes = scenes.ToArray();
        var identityCapacity = allScenes.Sum(scene =>
            (scene.Hub is null ? 0 : allScenes.Length - 1)
            + SceneSteps.Walk(scene.Steps).Sum(step => step switch
            {
                DivertStep { Target.IsEnd: false } => 1,
                CheckStep check => (check.Check.Success.Divert is { IsEnd: false } ? 1 : 0)
                    + (check.Check.Failure.Divert is { IsEnd: false } ? 1 : 0),
                _ => 0,
            }));
        var buffer = new ArrayBufferWriter<byte>();
        var replayIdentityData = new ArrayBufferWriter<byte>(Math.Max(1, identityCapacity * CompactLength));
        var replayIdentityCache = new DivertReplayIdentityCache(identityCapacity);
        using var writer = new Utf8JsonWriter(buffer);
        var descriptions = new Dictionary<ulong, (int Start, int Length)>();
        var choices = new List<ChoiceDefinition>();
        var compactCollisions = false;
        foreach (var scene in allScenes)
        {
            buffer.Clear();
            descriptions.Clear();
            choices.Clear();
            scene.ReplayIdentityCache = replayIdentityCache;
            compactCollisions = false;
            foreach (var step in SceneSteps.Walk(scene.Steps))
            {
                if (step is ChoiceStep choice)
                {
                    Add(scene, choice.Choice.Divert?.Scene, choice.Choice.Effects, choice.Choice.Check, choice.Choice);
                }
            }
            foreach (var target in SyntheticTargets(scene, allScenes))
            {
                if (!replayIdentityCache.Offsets.ContainsKey((scene.Id, target)))
                {
                    Add(scene, target, effects: [], check: null, choice: null);
                }
            }
            scene.UsesFullReplayIdentities = compactCollisions;
            if (compactCollisions)
            {
                var targets = replayIdentityCache.Offsets.Keys
                    .Where(key => key.Source == scene.Id)
                    .Select(key => key.Target)
                    .ToArray();
                if (targets.Length > 0)
                {
                    var digest = new byte[FullLength];
                    foreach (var target in targets)
                    {
                        var description = Describe(scene.Id, target, effects: [], check: null);
                        if (hash is null)
                        {
                            SHA256.HashData(description, digest);
                        }
                        else
                        {
                            hash(description).CopyTo(digest);
                        }
                        replayIdentityCache.Offsets[(scene.Id, target)] = replayIdentityData.WrittenCount;
                        digest.CopyTo(replayIdentityData.GetSpan(FullLength));
                        replayIdentityData.Advance(FullLength);
                    }
                }
            }
            else
            {
                foreach (var choice in choices)
                {
                    choice.ReplayIdentity = choice.ReplayIdentity![..CompactLength];
                }
            }
        }
        replayIdentityCache.Data = replayIdentityData.WrittenMemory;

        void Add(SceneDefinition scene, SceneId? divert, IReadOnlyList<Effect> effects, CheckBranch? check,
            ChoiceDefinition? choice)
        {
            var start = buffer.WrittenCount;
            writer.Reset(buffer);
            WriteDescription(writer, scene.Id, divert, effects, check);
            writer.Flush();
            var description = buffer.WrittenSpan[start..];
            Span<byte> digest = stackalloc byte[FullLength];
            if (hash is null)
            {
                SHA256.HashData(description, digest);
            }
            else
            {
                hash(description.ToArray()).CopyTo(digest);
            }
            var prefix = BinaryPrimitives.ReadUInt64LittleEndian(digest);
            if (descriptions.TryGetValue(prefix, out var previous))
            {
                if (!description.SequenceEqual(buffer.WrittenSpan.Slice(previous.Start, previous.Length)))
                {
                    compactCollisions = true;
                }
            }
            else
            {
                descriptions.Add(prefix, (start, description.Length));
            }
            if (choice is not null)
            {
                choice.ReplayIdentity = digest.ToArray();
                choices.Add(choice);
            }
            else if (divert is { } target)
            {
                var key = (scene.Id, target);
                replayIdentityCache.Offsets.Add(key, replayIdentityData.WrittenCount);
                digest[..CompactLength].CopyTo(replayIdentityData.GetSpan(CompactLength));
                replayIdentityData.Advance(CompactLength);
            }
        }
    }

    internal static ReadOnlyMemory<byte> ForDivert(SceneDefinition source, DivertTarget target)
    {
        if (target.Scene is { } targetScene
            && source.ReplayIdentityCache is { } cache
            && cache.TryGet(source.Id, targetScene, source.UsesFullReplayIdentities, out var identity))
        {
            return identity;
        }

        throw new InvalidOperationException(
            $"Replay identity for divert from '{source.Id}' to '{target}' was not precomputed.");
    }

    private static IEnumerable<SceneId> SyntheticTargets(SceneDefinition scene,
        IReadOnlyList<SceneDefinition> allScenes)
    {
        if (scene.Hub is not null)
        {
            foreach (var storylet in allScenes)
            {
                if (storylet.Id != scene.Id)
                {
                    yield return storylet.Id;
                }
            }
        }

        foreach (var step in SceneSteps.Walk(scene.Steps))
        {
            switch (step)
            {
                case DivertStep { Target.Scene: { } target }:
                    yield return target;
                    break;
                case CheckStep check:
                    if (check.Check.Success.Divert?.Scene is { } success) yield return success;
                    if (check.Check.Failure.Divert?.Scene is { } failure) yield return failure;
                    break;
            }
        }
    }

    private static byte[] Describe(SceneId scene, SceneId? divert,
        IReadOnlyList<Effect> effects, CheckBranch? check)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteDescription(writer, scene, divert, effects, check);
        }
        return stream.ToArray();
    }

    private static void WriteDescription(Utf8JsonWriter writer, SceneId scene, SceneId? divert,
        IReadOnlyList<Effect> effects, CheckBranch? check)
    {
        writer.WriteStartArray();
        writer.WriteStringValue("choice-replay-v1");
        writer.WriteStringValue(scene.Value);
        writer.WriteStringValue(divert?.Value);
        writer.WriteStartArray();
        foreach (var effect in effects)
        {
            WriteEffect(writer, effect);
        }
        writer.WriteEndArray();
        WriteCheck(writer, check);
        writer.WriteEndArray();
    }

    private static void WriteDivert(Utf8JsonWriter writer, DivertTarget? target)
    {
        if (target is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(target.Scene?.Value);
        }
    }

    private static void WriteCheck(Utf8JsonWriter writer, CheckBranch? check)
    {
        if (check is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        writer.WriteStringValue(check.Check.Actor.Value);
        writer.WriteStringValue(check.Check.Skill.Value);
        WriteExpr(writer, check.Check.Difficulty);
        WriteOutcome(writer, check.Success);
        WriteOutcome(writer, check.Failure);
        writer.WriteEndArray();
    }

    private static void WriteOutcome(Utf8JsonWriter writer, CheckOutcome outcome)
    {
        writer.WriteStartArray();
        writer.WriteStartArray();
        foreach (var effect in outcome.Effects)
        {
            WriteEffect(writer, effect);
        }
        writer.WriteEndArray();
        WriteDivert(writer, outcome.Divert);
        writer.WriteEndArray();
    }

    private static void WriteEffect(Utf8JsonWriter writer, Effect effect)
    {
        writer.WriteStartArray();
        switch (effect)
        {
            case ChangeStat change:
                writer.WriteStringValue("stat");
                writer.WriteStringValue(change.Character.Value);
                writer.WriteStringValue(change.Stat.Value);
                writer.WriteStringValue(change.Op.ToString());
                WriteExpr(writer, change.Amount);
                break;
            case ChangeStanding change:
                writer.WriteStringValue(ExpressionVocabulary.Standing);
                writer.WriteStringValue(change.Faction.Value);
                writer.WriteStringValue(change.Op.ToString());
                WriteExpr(writer, change.Amount);
                break;
            case ChangeVariable change:
                writer.WriteStringValue("variable");
                writer.WriteStringValue(change.Variable.Value);
                writer.WriteStringValue(change.Op.ToString());
                WriteExpr(writer, change.Amount);
                break;
            case SetTextVariable change:
                writer.WriteStringValue("text-variable");
                writer.WriteStringValue(change.Variable.Value);
                WriteExpr(writer, change.Value);
                break;
            case SetFlag change:
                writer.WriteStringValue(ExpressionVocabulary.Flag);
                writer.WriteStringValue(change.Flag.Value);
                writer.WriteBooleanValue(change.Value);
                break;
            case GiveItem change:
                writer.WriteStringValue("give-item");
                writer.WriteStringValue(change.Character.Value);
                writer.WriteStringValue(change.Item.Value);
                WriteExpr(writer, change.Quantity);
                break;
            case TakeItem change:
                writer.WriteStringValue("take-item");
                writer.WriteStringValue(change.Character.Value);
                writer.WriteStringValue(change.Item.Value);
                WriteExpr(writer, change.Quantity);
                break;
            default:
                throw new NotSupportedException($"Unknown choice effect '{effect.GetType().Name}'.");
        }
        writer.WriteEndArray();
    }

    private static void WriteExpr(Utf8JsonWriter writer, Expr expression)
    {
        writer.WriteStartArray();
        switch (expression)
        {
            case NumberLiteral value:
                writer.WriteStringValue("number");
                writer.WriteNumberValue(value.Value);
                break;
            case BoolLiteral value:
                writer.WriteStringValue("bool");
                writer.WriteBooleanValue(value.Value);
                break;
            case TextLiteral value:
                writer.WriteStringValue("text");
                writer.WriteStringValue(value.Value);
                break;
            case TierLiteral value:
                writer.WriteStringValue(ExpressionVocabulary.Tier);
                writer.WriteStringValue(value.Tier.Id);
                break;
            case StatRef value:
                writer.WriteStringValue("stat-ref");
                writer.WriteStringValue(value.Character.Value);
                writer.WriteStringValue(value.Stat.Value);
                break;
            case StandingRef value:
                writer.WriteStringValue("standing-ref");
                writer.WriteStringValue(value.Faction.Value);
                break;
            case StandingTierRef value:
                writer.WriteStringValue("standing-tier-ref");
                writer.WriteStringValue(value.Faction.Value);
                break;
            case FlagRef value:
                writer.WriteStringValue("flag-ref");
                writer.WriteStringValue(value.Flag.Value);
                break;
            case VarRef value:
                writer.WriteStringValue("variable-ref");
                writer.WriteStringValue(value.Variable.Value);
                break;
            case TextVarRef value:
                writer.WriteStringValue("text-variable-ref");
                writer.WriteStringValue(value.Variable.Value);
                break;
            case NameRef value:
                writer.WriteStringValue("name-ref");
                writer.WriteStringValue(value.Character.Value);
                writer.WriteStringValue(value.Part.ToString());
                break;
            case PronounRef value:
                writer.WriteStringValue("pronoun-ref");
                writer.WriteStringValue(value.Character.Value);
                writer.WriteStringValue(value.Form.ToString());
                break;
            case FactionNameRef value:
                writer.WriteStringValue("faction-name-ref");
                writer.WriteStringValue(value.Faction.Value);
                break;
            case ItemNameRef value:
                writer.WriteStringValue("item-name-ref");
                writer.WriteStringValue(value.Item.Value);
                break;
            case HasItem value:
                writer.WriteStringValue("has-item");
                writer.WriteStringValue(value.Character.Value);
                writer.WriteStringValue(value.Item.Value);
                break;
            case Visited value:
                writer.WriteStringValue(ExpressionVocabulary.Visited);
                writer.WriteStringValue(value.Scene.Value);
                break;
            case SceneVisitCountRef value:
                writer.WriteStringValue("scene-visit-count-ref");
                writer.WriteStringValue(value.Scene?.Value);
                break;
            case NamedPredicate value:
                writer.WriteStringValue("predicate");
                writer.WriteStringValue(value.Name);
                break;
            case UnaryExpr value:
                writer.WriteStringValue("unary");
                writer.WriteStringValue(value.Op.ToString());
                WriteExpr(writer, value.Operand);
                break;
            case BinaryExpr value:
                writer.WriteStringValue("binary");
                writer.WriteStringValue(value.Op.ToString());
                WriteExpr(writer, value.Left);
                WriteExpr(writer, value.Right);
                break;
            default:
                throw new NotSupportedException($"Unknown choice expression '{expression.GetType().Name}'.");
        }
        writer.WriteEndArray();
    }
}

internal sealed class DivertReplayIdentityCache
{
    internal DivertReplayIdentityCache(int capacity)
    {
        Offsets = new Dictionary<(SceneId Source, SceneId Target), int>(capacity);
    }

    internal Dictionary<(SceneId Source, SceneId Target), int> Offsets { get; }
    internal ReadOnlyMemory<byte> Data { get; set; }

    internal bool TryGet(SceneId source, SceneId target, bool fullLength,
        out ReadOnlyMemory<byte> identity)
    {
        if (Offsets.TryGetValue((source, target), out var offset))
        {
            identity = Data.Slice(offset, fullLength
                ? ChoiceReplayIdentity.FullLength
                : ChoiceReplayIdentity.CompactLength);
            return true;
        }

        identity = default;
        return false;
    }
}
