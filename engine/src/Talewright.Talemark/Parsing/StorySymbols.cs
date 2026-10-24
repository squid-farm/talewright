using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Narrative.Templates;
using Talewright.Rules.Expressions;
using Talewright.Talemark.Analysis;

namespace Talewright.Talemark.Parsing;

/// <summary>
/// Everything a reference can resolve to. Built from story.talewright.yaml plus syntax discovery across all .tale files
/// (scene ids, and flags/variables that some effect assigns).
/// </summary>
internal sealed class StorySymbols
{
    public AnalysisCollector? Analysis { get; init; }
    public required WorldDefinition World { get; init; }
    public required IReadOnlySet<FlagId> Flags { get; init; }
    public required IReadOnlySet<VarId> Variables { get; init; }
    public required IReadOnlySet<SceneId> Scenes { get; init; }
    public required NamedPredicateRegistry Predicates { get; init; }
    public required TextModifierRegistry Modifiers { get; init; }
}
