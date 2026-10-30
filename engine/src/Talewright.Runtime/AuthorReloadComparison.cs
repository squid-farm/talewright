using System.Collections;
using System.Reflection;
using Talewright.Core.World;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Rulesets.Generic;
using Talewright.Talemark.Model;

namespace Talewright.Runtime;

internal static class AuthorReloadComparison
{
    public static void Require(bool compatible, string reason)
    {
        if (!compatible)
        {
            throw new AuthorReloadException(reason);
        }
    }

    public static void Schema(StoryDefinition original, StoryDefinition candidate)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(candidate);
        foreach (var story in new[] { original, candidate })
        {
            var rules = story.World.Ruleset;
            Require(!story.Predicates.Names.Any()
                && (rules.SkillCheckRule is null or D20SkillCheckRule)
                && (rules.LevelingRule is null or ExperienceLevelingRule)
                && rules.Modifiers.Count == 0, "custom extensions require trusted rebuild/restart");
            Require(story.Scenes.All(scene => BuiltInBlocks(SceneSteps.Walk(scene.Steps).OfType<ProseStep>().Select(step => step.Block))
                && scene.Choices.All(choice => choice.Label is null || BuiltInInlines(choice.Label))),
                "custom text modifiers require trusted rebuild/restart");
        }
        Require(original.Id == candidate.Id && original.Version == candidate.Version
            && original.Start == candidate.Start && original.Randomness == candidate.Randomness
            && original.Seed == candidate.Seed && original.UndoEnabled == candidate.UndoEnabled
            && Equal(original.Creation, candidate.Creation)
            && Equal(original.World, candidate.World) && Equal(original.Triggers, candidate.Triggers),
            "world declarations, creation, triggers or rules changed (including Talemark auto-declarations)");
    }

    private static bool BuiltInBlocks(IEnumerable<TemplateBlock> blocks) => blocks.All(block => block switch
    {
        ParagraphTemplate paragraph => BuiltInInlines(paragraph.Inlines),
        SpeechTemplate speech => BuiltInInlines(speech.Inlines),
        ConditionalBlockTemplate conditional => BuiltInBlocks(conditional.Then) && BuiltInBlocks(conditional.Else),
        _ => false,
    });

    private static bool BuiltInInlines(IEnumerable<TemplateInline> inlines) => inlines.All(inline => inline switch
    {
        SubstitutionTemplate substitution => substitution.Modifiers.All(TextModifierRegistry.IsBuiltIn),
        StyledTemplate styled => BuiltInInlines(styled.Children),
        ConditionalInlineTemplate conditional => BuiltInInlines(conditional.Then) && BuiltInInlines(conditional.Else),
        TextTemplate or LineBreakTemplate or MediaTemplate => true,
        _ => false,
    });

    // Conservative structural equality for immutable engine data, including polymorphic expression,
    // effect and template nodes. Collection order is significant; unknown extension types fail closed.
    public static bool Equal(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        if (left is ITextModifier || right is ITextModifier)
        {
            if (left is OneManyTextModifier selector)
            {
                return right is OneManyTextModifier other && selector == other;
            }

            return left is ITextModifier modifier && TextModifierRegistry.IsBuiltIn(modifier)
                && ReferenceEquals(left, right);
        }
        var type = left.GetType();
        if (type.IsPrimitive || type.IsEnum || left is string or decimal)
        {
            return left.Equals(right);
        }
        if (left is IEnumerable a && right is IEnumerable b)
        {
            var x = a.GetEnumerator();
            var y = b.GetEnumerator();
            try
            {
                while (x.MoveNext())
                {
                    if (!y.MoveNext() || !Equal(x.Current, y.Current))
                    {
                        return false;
                    }
                }
                return !y.MoveNext();
            }
            finally
            {
                (x as IDisposable)?.Dispose();
                (y as IDisposable)?.Dispose();
            }
        }
        if (type != right.GetType())
        {
            return false;
        }
        var assembly = type.Assembly;
        if (assembly != typeof(WorldDefinition).Assembly && assembly != typeof(RenderedBlock).Assembly
            && assembly != typeof(Expr).Assembly && assembly != typeof(StoryDefinition).Assembly
            && assembly != typeof(GenericRuleset).Assembly
            && type != typeof(AuthorStoryletChoice)
            && !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)))
        {
            return false;
        }
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Where(property => left is not SceneStep || property.Name is not (nameof(SceneStep.Id) or nameof(SceneStep.Source)))
            .All(property => Equal(property.GetValue(left), property.GetValue(right)));
    }
}
