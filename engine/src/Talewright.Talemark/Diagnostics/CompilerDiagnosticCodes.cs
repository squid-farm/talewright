namespace Talewright.Talemark.Diagnostics;

/// <summary>Stable diagnostic codes emitted by Talemark compilation.</summary>
public static class CompilerDiagnosticCodes
{
    public const string InvalidExpression = "TW1001";
    public const string NumberTooLarge = "TW1002";
    public const string MalformedEffect = "TW1003";
    public const string UnclosedInlineConstruct = "TW1004";
    public const string UnbalancedCondition = "TW1005";
    public const string InvalidSceneMetadata = "TW1006";
    public const string MalformedDialogue = "TW1007";
    public const string InvalidSceneId = "TW1008";
    public const string MalformedSkillCheckOutcome = "TW1009";
    public const string MissingDivertTarget = "TW1010";
    public const string MalformedChoice = "TW1011";
    public const string InvalidBlock = "TW1012";
    public const string MalformedSkillCheck = "TW1013";
    public const string InvalidImageCall = "TW1014";
    public const string LegacyImageSyntax = "TW1015";
    public const string NestingLimitExceeded = "TW1016";
    public const string TrailingReaderTextBackslash = "TW1017";

    public const string UnknownNameOrProperty = "TW2001";
    public const string UnknownScene = "TW2002";
    public const string TypeMismatch = "TW2003";
    public const string UnsetFlagOrUnassignedVariable = "TW2004";
    public const string UnknownPredicate = "TW2005";
    public const string InvalidModifier = "TW2006";
    public const string UnresolvedVariableType = "TW2007";
    public const string UnsafeImagePath = "TW2010";
    public const string UnsupportedImageFormat = "TW2011";
    public const string MissingImage = "TW2012";
    public const string AmbiguousImagePath = "TW2013";
    public const string MissingImageAlt = "TW2014";
    public const string DecorativeImageHasAlt = "TW2015";
    public const string MissingAccessibleChoiceLabel = "TW2016";

    public const string TextOutsideSceneOrNoFiles = "TW3001";
    public const string DeadEndScene = "TW3002";
    public const string MissingChoiceTarget = "TW3003";
    public const string SkillChecksUnsupported = "TW3005";
    public const string MissingStartScene = "TW3007";
    public const string EmptyHub = "TW3008";
    public const string UnreachableScene = "TW3009";
    public const string DuplicateScene = "TW3010";
    public const string StepAfterChoice = "TW3011";
    public const string UnreachableStep = "TW3012";
    public const string MayHaveNoAvailableChoice = "TW3013";
    public const string SceneCharacterIdCollision = "TW3014";

    public const string InvalidYaml = "TW4001";
    public const string MissingRequiredConfiguration = "TW4002";
    public const string InvalidConfigurationValue = "TW4003";
    public const string UnknownRuleset = "TW4004";
    public const string UnknownConfigurationReference = "TW4005";
    public const string UnknownConfigurationKey = "TW4006";
    public const string DuplicateConfigurationDeclaration = "TW4007";
}
