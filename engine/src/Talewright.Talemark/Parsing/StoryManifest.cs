using Talewright.Core.Identity;
using Talewright.Core.World;
using Talewright.Rules.Checks;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Parsing;

/// <summary>An expression or effect list written in story configuration, parsed later once all symbols are known.</summary>
internal sealed record RawTrigger(string Id, string When, SourceLocation WhenLocation, string Do, SourceLocation DoLocation, bool Once, bool Save);

/// <summary>Core settings and declarations assembled from the story's configuration inputs.</summary>
internal sealed class StoryManifest
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Version { get; set; } = "0.0.0";
    public string Start { get; set; } = string.Empty;
    public SourceLocation StartLocation { get; set; }
    public RandomnessMode Randomness { get; set; } = RandomnessMode.ChecksOnly;
    public ulong? Seed { get; set; }
    public bool UndoEnabled { get; set; } = true;
    public bool AutosaveEnabled { get; set; } = true;
    public CharacterSheetVisibility CharacterSheet { get; set; } = CharacterSheetVisibility.Shown;
    public CharacterCreation? Creation { get; set; }
    public required WorldDefinition World { get; init; }
    public IReadOnlyDictionary<EntityId, SourceLocation> CharacterLocations { get; init; } =
        new Dictionary<EntityId, SourceLocation>();
    public List<RawTrigger> Triggers { get; } = [];

    /// <summary>Raw trigger text, for the flag/variable pre-scan.</summary>
    public IEnumerable<string> EffectText => Triggers.Select(t => t.Do);
}
