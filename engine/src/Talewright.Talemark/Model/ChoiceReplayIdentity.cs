using System.Security.Cryptography;
using System.Text.Json;
using Talewright.Core.Identity;
using Talewright.Rules.Checks;
using Talewright.Rules.Effects;
using Talewright.Rules.Expressions;

namespace Talewright.Talemark.Model;

internal static class ChoiceReplayIdentity
{
    internal const int CompactLength = 8;
    internal const int FullLength = 32;

    internal static void Assign(IEnumerable<SceneDefinition> scenes) =>
        Assign(scenes, SHA256.HashData);

    internal static void Assign(IEnumerable<SceneDefinition> scenes, Func<byte[], byte[]> hash)
    {
        var allScenes = scenes.ToArray();
        foreach (var scene in allScenes)
        {
            var choices = SceneSteps.Walk(scene.Steps).OfType<ChoiceStep>().ToArray();
            var descriptions = choices.Select(step => Describe(scene.Id, step.Choice)).ToArray();
            var collisionDescriptions = descriptions.Concat(SyntheticDescriptions(scene, allScenes)).ToArray();
            var collisionHashes = collisionDescriptions.Select(hash).ToArray();
            var compactCollisions = collisionHashes
                .Select((value, index) => (Prefix: value[..CompactLength], Description: collisionDescriptions[index]))
                .GroupBy(entry => Convert.ToHexString(entry.Prefix), StringComparer.Ordinal)
                .Any(group => group.Select(entry => Convert.ToBase64String(entry.Description))
                    .Distinct(StringComparer.Ordinal).Count() > 1);
            scene.UsesFullReplayIdentities = compactCollisions;
            for (var index = 0; index < choices.Length; index++)
            {
                choices[index].Choice.ReplayIdentity = compactCollisions
                    ? collisionHashes[index]
                    : collisionHashes[index][..CompactLength];
            }
        }
    }

    internal static byte[] ForDivert(SceneDefinition source, DivertTarget target)
    {
        var hash = SHA256.HashData(Describe(source.Id, target, effects: [], check: null));
        return source.UsesFullReplayIdentities ? hash : hash[..CompactLength];
    }

    private static IEnumerable<byte[]> SyntheticDescriptions(SceneDefinition scene,
        IReadOnlyList<SceneDefinition> allScenes)
    {
        if (scene.Hub is { } hub)
        {
            foreach (var storylet in allScenes.Where(candidate =>
                candidate.Id != scene.Id && hub.Matches(candidate.Tags)))
            {
                yield return Describe(scene.Id, new DivertTarget(storylet.Id), effects: [], check: null);
            }
        }

        foreach (var step in SceneSteps.Walk(scene.Steps))
        {
            IEnumerable<DivertTarget?> targets = step switch
            {
                DivertStep divert => [divert.Target],
                CheckStep check => [check.Check.Success.Divert, check.Check.Failure.Divert],
                _ => [],
            };
            foreach (var target in targets)
            {
                if (target is { IsEnd: false })
                {
                    yield return Describe(scene.Id, target, effects: [], check: null);
                }
            }
        }
    }

    private static byte[] Describe(SceneId scene, ChoiceDefinition choice) =>
        Describe(scene, choice.Divert, choice.Effects, choice.Check);

    private static byte[] Describe(SceneId scene, DivertTarget? divert,
        IReadOnlyList<Effect> effects, CheckBranch? check)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("choice-replay-v1");
            writer.WriteStringValue(scene.Value);
            WriteDivert(writer, divert);
            writer.WriteStartArray();
            foreach (var effect in effects)
            {
                WriteEffect(writer, effect);
            }
            writer.WriteEndArray();
            WriteCheck(writer, check);
            writer.WriteEndArray();
        }
        return stream.ToArray();
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
                writer.WriteStringValue("standing");
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
                writer.WriteStringValue("flag");
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
                writer.WriteStringValue("tier");
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
                writer.WriteStringValue("visited");
                writer.WriteStringValue(value.Scene.Value);
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
