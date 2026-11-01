using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Talewright.Core.Identity;

namespace Talewright.Testing;

public sealed record TaleVariableRef
{
    public TaleVariableRef(string id) => Id = FakerIdentifier.Require(id, nameof(id));
    public string Id { get; }
    public override string ToString() => $"var:{Id}";
}

public sealed record TaleFlagRef
{
    public TaleFlagRef(string id) => Id = FakerIdentifier.Require(id, nameof(id));
    public string Id { get; }
    public override string ToString() => $"flag:{Id}";
}

public sealed record TaleItemRef
{
    public TaleItemRef(string id) => Id = FakerIdentifier.Require(id, nameof(id));
    public string Id { get; }
    public override string ToString() => Id;
}

public sealed record TaleCharacterRef
{
    public TaleCharacterRef(string id) => Id = FakerIdentifier.Require(id, nameof(id));

    public static TaleCharacterRef Player { get; } = new("player");

    public string Id { get; }
    public string Field(string name) => $"{Id}:{FakerIdentifier.Require(name, nameof(name))}";
    public override string ToString() => Id;
}

public sealed record TaleFactionRef
{
    public TaleFactionRef(string id) => Id = FakerIdentifier.Require(id, nameof(id));
    public string Id { get; }
    public override string ToString() => $"faction:{Id}";
}

internal static class FakerIdentifier
{
    public static string Require(string id, string parameter)
    {
        ArgumentNullException.ThrowIfNull(id, parameter);
        if (!IdentifierRules.IsValid(id))
        {
            throw new ArgumentException($"'{id}' is not a valid Talemark identifier.", parameter);
        }

        return id;
    }
}

internal static class FakerArgument
{
    public static T Required<T>(T? value, [CallerArgumentExpression(nameof(value))] string? parameter = null)
        where T : class => value ?? throw new ArgumentNullException(parameter);
}

public static class ExpressionFaker
{
    public const char OpenParenthesis = '(';
    public const char CloseParenthesis = ')';
    public const char UnaryMinus = '-';

    public static string AtLeast(string reference, int value) =>
        FormattableString.Invariant($"{FakerArgument.Required(reference)} >= {value}");

    public static string AtLeast(TaleVariableRef variable, int value) =>
        AtLeast(FakerArgument.Required(variable).ToString(), value);

    public static string GreaterThan(string reference, int value) =>
        FormattableString.Invariant($"{FakerArgument.Required(reference)} > {value}");

    public static string GreaterThan(TaleVariableRef variable, int value) =>
        GreaterThan(FakerArgument.Required(variable).ToString(), value);

    public static string EqualTo(string reference, int value) =>
        FormattableString.Invariant($"{FakerArgument.Required(reference)} == {value}");

    public static string EqualTo(TaleVariableRef variable, int value) =>
        EqualTo(FakerArgument.Required(variable).ToString(), value);

    public static string Not(string condition) => $"not {FakerArgument.Required(condition)}";
    public static string Not(TaleFlagRef flag) => Not(FakerArgument.Required(flag).ToString());

    public static string HasItem(TaleCharacterRef character, TaleItemRef item) =>
        $"{FakerArgument.Required(character)} has {FakerArgument.Required(item)}";

    public static string Parenthesize(string expression) =>
        OpenParenthesis + FakerArgument.Required(expression) + CloseParenthesis;

    public static string Negate(string expression) => UnaryMinus + FakerArgument.Required(expression);
}

public static class EffectFaker
{
    public static string Assign(TaleVariableRef variable, int value) =>
        Assign(variable, value.ToString(CultureInfo.InvariantCulture));

    public static string Assign(TaleVariableRef variable, string expression) =>
        $"{FakerArgument.Required(variable)} = {FakerArgument.Required(expression)}";

    public static string Increase(TaleVariableRef variable, int value) =>
        FormattableString.Invariant($"{FakerArgument.Required(variable)} += {value}");

    public static string Decrease(string reference, int value) =>
        FormattableString.Invariant($"{FakerArgument.Required(reference)} -= {value}");

    public static string Set(TaleFlagRef flag) => $"set {FakerArgument.Required(flag)}";
    public static string Clear(TaleFlagRef flag) => $"clear {FakerArgument.Required(flag)}";
    public static string Give(TaleCharacterRef character, TaleItemRef item) =>
        $"give {FakerArgument.Required(character)} {FakerArgument.Required(item)}";
    public static string Line(string effect) => $"~ {FakerArgument.Required(effect)}";
    public static string Inline(string effects) => $"{{ {FakerArgument.Required(effects)} }}";
}

public static class IfFaker
{
    public const string Else = "{else}";
    public const string Close = "{/if}";

    public static string Open(string condition) => $"{{if {FakerArgument.Required(condition)}}}";
    public static string Open(TaleFlagRef flag) => Open(FakerArgument.Required(flag).ToString());
    public static string Inline(string condition, string content) => Open(condition) + FakerArgument.Required(content) + Close;
    public static string Inline(TaleFlagRef flag, string content) => Inline(FakerArgument.Required(flag).ToString(), content);
    public static string Inline(string condition, string content, string elseContent) =>
        Open(condition) + FakerArgument.Required(content) + Else + FakerArgument.Required(elseContent) + Close;
    public static string Inline(TaleFlagRef flag, string content, string elseContent) =>
        Inline(FakerArgument.Required(flag).ToString(), content, elseContent);
    public static string Block(string condition, string content) => $"{Open(condition)}\n{FakerArgument.Required(content)}\n{Close}";
    public static string Block(TaleFlagRef flag, string content) => Block(FakerArgument.Required(flag).ToString(), content);
}

public static class SubstitutionFaker
{
    public static string Variable(TaleVariableRef variable) => Wrap(FakerArgument.Required(variable).ToString());
    public static string Character(TaleCharacterRef character, string field) => Wrap(FakerArgument.Required(character).Field(field));
    public static string Item(TaleItemRef item) => Wrap($"item:{FakerArgument.Required(item).Id}");
    public static string Wrap(string reference) => $"{{{FakerArgument.Required(reference)}}}";
}

public static class DivertFaker
{
    public static string To(string target) => $"-> {FakerIdentifier.Require(target, nameof(target))}";
}

public static class ChoiceFaker
{
    public const string LabelPrefix = "* [";

    public static string Open(string label) => $"{LabelPrefix}{FakerArgument.Required(label)}]";

    public static string Create(string target, string? label = null, string? requires = null, string? effect = null)
    {
        var choice = label is null ? "*" : Open(label);
        var condition = requires is null ? "" : $" (requires: {requires})";
        var effects = effect is null ? "" : $" {EffectFaker.Inline(effect)}";
        return $"{choice}{condition}{effects} {DivertFaker.To(target)}";
    }
}

public static class SceneFaker
{
    public const string TitlePrefix = "@title ";
    public const string Once = "@once";

    public static string Header(string id) => $"=== {FakerIdentifier.Require(id, nameof(id))}";
    public static string Title(string title) => TitlePrefix + FakerArgument.Required(title);
    public static string Tags(params string[] tags) => Metadata("tags", string.Join(", ", tags));
    public static string Priority(int priority) => Metadata("priority", priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static string Requires(string condition) => Metadata("requires", condition);
    public static string Hub(string query) => Metadata("hub", query);
    public static string HubAny(params string[] tags) => Hub($"any({string.Join(", ", tags)})");
    public static string HubLimit(int limit) => Metadata("hub-limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public static string Speaker(TaleCharacterRef character, string text) =>
        $"> {FakerArgument.Required(character)}: {FakerArgument.Required(text)}";

    private static string Metadata(string name, string value) => $"@{name} {FakerArgument.Required(value)}";
}

public static class MarkupFaker
{
    public const string EmphasisDelimiter = "*";
    public const string StrongDelimiter = "**";
    public const string CodeDelimiter = "`";
    public const string LinkClose = ")";

    public static string Strong(string text) => StrongDelimiter + FakerArgument.Required(text) + StrongDelimiter;
    public static string EmphasisOpen(string text) => EmphasisDelimiter + FakerArgument.Required(text);
    public static string Emphasis(string text) => EmphasisOpen(text) + EmphasisDelimiter;
    public static string Code(string text) => CodeDelimiter + FakerArgument.Required(text) + CodeDelimiter;
    public static string LinkOpen(string label) => $"[{FakerArgument.Required(label)}](";
    public static string Link(string label, string target) => LinkOpen(label) + FakerArgument.Required(target) + LinkClose;
}

public static class ImageFaker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Call(string path, string? alt = null) =>
        alt is null
            ? $"{{image({JsonSerializer.Serialize(FakerArgument.Required(path), JsonOptions)})}}"
            : $"{{image({JsonSerializer.Serialize(FakerArgument.Required(path), JsonOptions)}, {JsonSerializer.Serialize(alt, JsonOptions)})}}";
}

public static class SkillCheckFaker
{
    public static string Choice(
        string label,
        string skill,
        int difficulty,
        string success,
        string failure,
        string? successEffect = null,
        string? failureEffect = null) =>
        FormattableString.Invariant(
            $"{ChoiceFaker.Open(label)} skill-check {FakerArgument.Required(skill)} dc {difficulty}\n  success: {Outcome(successEffect, success)}\n  failure: {Outcome(failureEffect, failure)}");

    public static string Entry(
        string skill,
        int difficulty,
        string? successEffect = null,
        string? failureEffect = null,
        string? successTarget = null,
        string? failureTarget = null) =>
        FormattableString.Invariant(
            $"::: skill-check {FakerArgument.Required(skill)} dc {difficulty}\nsuccess: {Outcome(successEffect, successTarget)}\nfailure: {Outcome(failureEffect, failureTarget)}\n:::");

    private static string Outcome(string? effect, string? target)
    {
        var formattedEffect = effect is null ? "" : EffectFaker.Inline(effect) + (target is null ? "" : " ");
        var formattedTarget = target is null ? "" : DivertFaker.To(target);
        return formattedEffect + formattedTarget;
    }
}
