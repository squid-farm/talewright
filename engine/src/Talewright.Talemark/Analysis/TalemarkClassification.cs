namespace Talewright.Talemark.Analysis;

/// <summary>A compiler-recognized Talemark syntax category, independent of editor presentation.</summary>
public enum TalemarkClassificationKind
{
    Comment,
    Keyword,
    Operator,
    Punctuation,
    Number,
    String,
    Identifier,
    Namespace,
    Property,
    Function,
    Modifier,
    Formatting,
    Escape,
    SceneIdentifier,
    Divert,
    StructuralMarker,
}

[Flags]
public enum TalemarkClassificationModifiers
{
    None = 0,
    Declaration = 1,
}

/// <summary>A source range classified by the Talemark parser without resolving story symbols.</summary>
public sealed record TalemarkClassification(
    SourceRange Range,
    TalemarkClassificationKind Kind,
    TalemarkClassificationModifiers Modifiers = TalemarkClassificationModifiers.None);
