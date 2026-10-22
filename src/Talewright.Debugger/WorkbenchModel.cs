using System.Globalization;
using Talewright.Core.Identity;
using Talewright.Runtime;
using Talewright.Tale.Compilation;
using Talewright.Tale.Diagnostics;
using Talewright.Tale.Model;

namespace Talewright.Debugger;

public enum SetupNumberKind { Variable, Stat, Inventory, Standing }

public sealed class SetupNumber(SetupNumberKind kind, string id, string label, int value, string? character = null)
{
    public SetupNumberKind Kind { get; } = kind;
    public string Id { get; } = id;
    public string? Character { get; } = character;
    public string Label { get; } = label;
    public bool Enabled { get; set; }
    public string Value { get; set; } = value.ToString(CultureInfo.InvariantCulture);
}

public sealed class SetupFlag(FlagId id)
{
    public FlagId Id { get; } = id;
    public bool Enabled { get; set; }
    public bool Value { get; set; }
}

/// <summary>UI-independent orchestration; every replacement is prepared before adoption.</summary>
public sealed class WorkbenchModel(string root)
{
    public string Root { get; } = root;
    public StoryDefinition? Story { get; private set; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];
    public string? Error { get; private set; }
    public bool SourceRejected { get; private set; }
    public AuthorSession? Session { get; private set; }
    public AuthorInspection? Inspection { get; private set; }
    public CharacterSelection? Character { get; private set; }
    public string Target { get; set; } = "";
    public string Seed { get; set; } = "";
    public IReadOnlyList<SetupNumber> Numbers { get; private set; } = [];
    public IReadOnlyList<SetupFlag> Flags { get; private set; } = [];

    public void Reload()
    {
        try
        {
            var compiled = StoryCompiler.Compile(StorySource.FromDirectory(Root));
            Diagnostics = compiled.Diagnostics;
            if (compiled.Story is not { } story)
            {
                SourceRejected = true;
                Error = "Story has compiler errors. Fix the files, then select Reload story. Any previous run is unchanged.";
                return;
            }
            var numbers = new List<SetupNumber>();
            foreach (var variable in story.World.Variables)
            {
                numbers.Add(new(SetupNumberKind.Variable, variable.Key.Value, $"var:{variable.Key}", variable.Value));
            }
            foreach (var faction in story.World.Factions.Values)
            {
                numbers.Add(new(SetupNumberKind.Standing, faction.Id.Value, $"faction:{faction.Id}", faction.InitialStanding));
            }
            foreach (var character in story.World.Characters.Values)
            {
                foreach (var stat in story.World.Ruleset.Stats)
                {
                    numbers.Add(new(SetupNumberKind.Stat, stat.Id.Value, $"{character.Id}:{stat.Id}",
                        character.Stats.GetValueOrDefault(stat.Id, stat.Default), character.Id.Value));
                }
                foreach (var item in story.World.Items.Values)
                {
                    numbers.Add(new(SetupNumberKind.Inventory, item.Id.Value, $"{character.Id} inventory:{item.Id}",
                        character.Inventory.GetValueOrDefault(item.Id), character.Id.Value));
                }
            }
            Story = story;
            Numbers = numbers.AsReadOnly();
            Flags = Array.AsReadOnly(story.World.Flags.Select(id => new SetupFlag(id)).ToArray());
            Target = story.Start.Value;
            Seed = story.Seed?.ToString(CultureInfo.InvariantCulture) ?? "";
            Character = null;
            Session = null;
            Inspection = null;
            SourceRejected = false;
            Error = null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SourceRejected = true;
            Error = $"Could not reload story: {ex.Message}. Any previous run is unchanged.";
        }
    }

    public IEnumerable<SceneDefinition> FindScenes(string query) => Story?.Scenes.Where(scene =>
        scene.Id.Value.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (scene.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) ?? [];

    public void SelectCharacter(CharacterSelection character) => Character = CharacterSelection.Validate(
        Story ?? throw new InvalidOperationException("Load a story first."), character);

    public void Start()
    {
        Attempt(() =>
        {
            var story = Story ?? throw new InvalidOperationException("Load a valid story first.");
            ulong? seed = null;
            if (!string.IsNullOrWhiteSpace(Seed))
            {
                if (!ulong.TryParse(Seed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    throw new ArgumentException("Seed must be an unsigned 64-bit integer, or empty for the story/random default.");
                }
                seed = parsed;
            }
            var variables = new Dictionary<VarId, int>();
            var standings = new Dictionary<FactionId, int>();
            var stats = new List<AuthorStatValue>();
            var inventory = new List<AuthorItemValue>();
            foreach (var field in Numbers.Where(field => field.Enabled))
            {
                if (!int.TryParse(field.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                {
                    throw new ArgumentException($"{field.Label} requires a 32-bit integer.");
                }
                switch (field.Kind)
                {
                    case SetupNumberKind.Variable: variables.Add(new(field.Id), value); break;
                    case SetupNumberKind.Standing: standings.Add(new(field.Id), value); break;
                    case SetupNumberKind.Stat: stats.Add(new(new(field.Character!), new(field.Id), value)); break;
                    case SetupNumberKind.Inventory: inventory.Add(new(new(field.Character!), new(field.Id), value)); break;
                    default: throw new InvalidOperationException($"Unsupported setup field {field.Kind}.");
                }
            }
            var candidate = AuthorSession.Create(story, new SceneId(Target), captureDecisions: true,
                new AuthorSetup
                {
                    Flags = Flags.Where(field => field.Enabled).ToDictionary(field => field.Id, field => field.Value),
                    Variables = variables, Standings = standings, Stats = stats, Inventory = inventory,
                }, seed, Character);
            var inspection = candidate.Inspect();
            Session = candidate;
            Inspection = inspection;
        });
    }

    public void Choose(string key) => Attempt(() =>
    {
        var session = Session ?? throw new InvalidOperationException("Start a scene first.");
        session.Choose(key);
        Inspection = session.Inspect();
    });

    private void Attempt(Action action)
    {
        try
        {
            action();
            Error = null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException
            or NotSupportedException or ArithmeticException)
        {
            Error = $"Playtest failed: {ex.Message}";
        }
    }
}
