using System.Globalization;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Runtime;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Model;
using Talewright.Player.Library;

namespace Talewright.Debugger;

public enum SetupNumberKind { Variable, Stat, Inventory, Standing }

public sealed class SetupNumber(SetupNumberKind kind, string id, string label, int value, string? character = null)
{
    public SetupNumberKind Kind { get; } = kind;
    public string Id { get; } = id;
    public string? Character { get; } = character;
    public string Label { get; } = label;
    public bool Enabled { get; set; }
    public string Initial { get; } = value.ToString(CultureInfo.InvariantCulture);
    public string Value { get; set; } = value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Sets the value and enables the field only while it differs from its initial value.</summary>
    public void Edit(string value)
    {
        Value = value;
        Enabled = value.Trim() != Initial;
    }
}

public sealed class SetupFlag(FlagId id, bool value = false)
{
    public FlagId Id { get; } = id;
    public bool Enabled { get; set; }
    public bool Initial { get; } = value;
    public bool Value { get; set; } = value;

    public void Edit(bool value)
    {
        Value = value;
        Enabled = value != Initial;
    }
}

public sealed class SetupText(VarId id, string value)
{
    public VarId Id { get; } = id;
    public string Label => $"var:{Id}";
    public bool Enabled { get; set; }
    public string Initial { get; } = value;
    public string Value { get; set; } = value;

    public void Edit(string value)
    {
        Value = value;
        Enabled = value != Initial;
    }
}

public sealed record AcceptedSceneSource(SceneId Scene, SceneSourceRange Range, string Text, int? HighlightLine = null);

/// <summary>UI-independent orchestration; every replacement is prepared before adoption.</summary>
public sealed class WorkbenchModel(string root, string? requestedScene = null) : IDisposable
{
    private StorySource? _acceptedSource;
    private string? _requestedScene = requestedScene;
    private readonly ulong _randomSeed = DeterministicDice.NewSeed();
    private string _seed = "";
    public StoryImages? Images { get; private set; }
    private readonly List<StoryImages> _retiredImages = [];
    public string Root { get; } = root;
    public StoryDefinition? Story { get; private set; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = [];
    public string? Error { get; private set; }
    public bool SourceRejected { get; private set; }
    public bool SourcePending { get; private set; }
    public StorySourceMap? AcceptedSourceMap { get; private set; }
    public AcceptedSceneSource? CurrentSource
    {
        get
        {
            return Session?.Current.Scene is { } scene ? AcceptedSourceFor(scene) : null;
        }
    }

    public AcceptedSceneSource? AcceptedSourceFor(SceneId scene, string? choiceKey = null)
    {
        if (Story is not { } story || AcceptedSourceMap is not { } map ||
            !map.Scenes.TryGetValue(scene, out var range))
        {
            return null;
        }

        int? highlightLine = null;
        if (choiceKey is not null)
        {
            var choiceStep = SceneSteps.Walk(story.GetScene(scene).Steps).OfType<ChoiceStep>()
                .SingleOrDefault(step => step.Choice.Key == choiceKey);
            if (choiceStep is null)
                return null;
            highlightLine = choiceStep.Source.Start.Line;
        }

        return new(scene, range, map.Source.Files[range.File], highlightLine);
    }
    public bool RestartRequired { get; private set; }
    public string? ReloadStatus { get; private set; }
    public int CreationRevision { get; private set; }
    public AuthorSession? Session { get; private set; }
    public AuthorInspection? Inspection { get; private set; }
    public IGameStateView PreviewState => Session?.State ?? StartingState();
    public CharacterSelection? Character { get; private set; }
    public string Target { get; set; } = requestedScene ?? "";
    public string? RequestedSceneNotice { get; private set; }
    public string Seed
    {
        get => _seed;
        set
        {
            if (_seed == value) return;
            _seed = value;
            Character = null;
            ++CreationRevision;
        }
    }
    public ulong CreationSeed => DeterministicDice.ResolveSeed(
        ulong.TryParse(Seed, NumberStyles.None, CultureInfo.InvariantCulture, out var seed) ? seed : null,
        Story?.Seed, _randomSeed);
    public IReadOnlyList<SetupNumber> Numbers { get; private set; } = [];
    public IReadOnlyList<SetupFlag> Flags { get; private set; } = [];
    public IReadOnlyList<SetupText> Texts { get; private set; } = [];
    public IReadOnlyList<SetupNumber> EditNumbers { get; private set; } = [];
    public IReadOnlyList<SetupFlag> EditFlags { get; private set; } = [];
    public IReadOnlyList<SetupText> EditTexts { get; private set; } = [];
    public string ScenarioPath { get; set; } = "";
    public bool ConfirmOverwrite { get; set; }
    public string PlayerSavePath { get; set; } = "";
    public string? ScenarioStatus { get; private set; }

    public async Task ExportScenario() => await AttemptFile(async () =>
    {
        var confirmed = ConfirmOverwrite;
        ConfirmOverwrite = false;
        var session = Session ?? throw new InvalidOperationException("Start a scene first.");
        var bytes = AuthorScenario.Export(session, CompilerOptions.Default.CompatibilityId!);
        var path = ScenarioPath;
        await AuthorScenarioFiles.ExportAsync(path, bytes, Root, confirmed);
        ScenarioStatus = $"Synthetic scenario exported: {Path.GetFullPath(path)}. No source or player slot was written.";
    });

    public Task ImportScenario() => LoadScenario(adopt: false);
    public Task AdoptScenario() => LoadScenario(adopt: true);

    private async Task LoadScenario(bool adopt) => await AttemptFile(async () =>
    {
        var bytes = await AuthorScenarioFiles.ReadAsync(ScenarioPath);
        var story = Story ?? throw new InvalidOperationException("Load a story first.");
        var candidate = AuthorScenario.Import(story, bytes, CompilerOptions.Default.CompatibilityId!, adopt);
        AdoptSession(candidate.Session);
        ScenarioStatus = candidate.Adopted
            ? "New synthetic scenario adopted to accepted source. " + string.Join(" ", candidate.Differences)
                + " Export to a new path to keep the original scenario."
            : "Exact synthetic scenario imported; complete replay and redo verified.";
        ConfirmOverwrite = false;
    });

    public async Task ImportPlayerSave() => await AttemptFile(async () =>
    {
        var bytes = await AuthorScenarioFiles.ReadAsync(PlayerSavePath);
        var story = Story ?? throw new InvalidOperationException("Load a story first.");
        var history = StoryHistory.Restore(story, bytes);
        var candidate = history.ForkAuthor();
        AdoptSession(candidate);
        ScenarioStatus = "Compatible player save read only and forked into a synthetic author timeline, including redo. Original save unchanged.";
        ConfirmOverwrite = false;
    });

    private void AdoptSession(AuthorSession candidate)
    {
        var inspection = candidate.Inspect();
        var setup = candidate.Setup;
        int? Requested(SetupNumber field) => field.Kind switch
        {
            SetupNumberKind.Variable => setup.Variables.TryGetValue(new(field.Id), out var value) ? value : null,
            SetupNumberKind.Standing => setup.Standings.TryGetValue(new(field.Id), out var value) ? value : null,
            SetupNumberKind.Stat => setup.Stats.SingleOrDefault(value => value.Character.Value == field.Character && value.Stat.Value == field.Id)?.Value,
            SetupNumberKind.Inventory => setup.Inventory.SingleOrDefault(value => value.Character.Value == field.Character && value.Item.Value == field.Id)?.Quantity,
            _ => throw new InvalidOperationException($"Unsupported setup field {field.Kind}."),
        };
        var numbers = Numbers.Select(field =>
        {
            var requested = Requested(field);
            return new SetupNumber(field.Kind, field.Id, field.Label, 0, field.Character)
            {
                Enabled = requested.HasValue,
                Value = requested?.ToString(CultureInfo.InvariantCulture) ?? field.Value,
            };
        }).ToArray();
        var flags = Flags.Select(flag => new SetupFlag(flag.Id)
        {
            Enabled = setup.Flags.ContainsKey(flag.Id),
            Value = setup.Flags.GetValueOrDefault(flag.Id),
        }).ToArray();
        var texts = Texts.Select(field => new SetupText(field.Id,
            setup.TextVariables.GetValueOrDefault(field.Id, field.Value))
        {
            Enabled = setup.TextVariables.ContainsKey(field.Id),
        }).ToArray();
        Numbers = numbers;
        Flags = flags;
        Texts = texts;
        Session = candidate;
        Inspection = inspection;
        Target = candidate.StartScene.Value;
        Seed = candidate.Seed.ToString(CultureInfo.InvariantCulture);
        Character = candidate.Character;
        ++CreationRevision;
        RefreshEdits();
    }

    private async Task AttemptFile(Func<Task> action)
    {
        try
        {
            await action();
            if (!SourceRejected)
            {
                Error = null;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or KeyNotFoundException or ArithmeticException)
        {
            ScenarioStatus = null;
            Error = $"Scenario operation failed: {ex.Message}. Any previous run and input files are unchanged.";
        }
    }

    public void Reload() => ApplySource(SourceCompilation.Load(Root), restart: true);

    public void SourceChanged() => SourcePending = true;

    public void RequireRestart()
    {
        RestartRequired = true;
        SourceRejected = true;
        ReloadStatus = "YAML changed or watcher history is uncertain. Explicit Restart from source is required; Talemark saves cannot adopt mixed source.";
    }

    public void WatchFailed(Exception exception)
    {
        SourcePending = false;
        RequireRestart();
        SourceRejected = true;
        Error = WatcherFailure.Describe(exception);
    }

    public bool ApplySource(SourceCompilation candidate, bool restart = false, bool yamlChanged = false,
        Func<Action, bool>? commit = null)
    {
        bool Publish(Action action)
        {
            if (commit is not null)
            {
                return commit(action);
            }
            action();
            return true;
        }
        try
        {
            if (!Publish(() => SourcePending = false))
            {
                return false;
            }
            var configurationChanged = yamlChanged || (_acceptedSource is not null && candidate.Source is { } source
                && !Configuration(source).SequenceEqual(Configuration(_acceptedSource)));
            if (configurationChanged && (restart || Session is null || candidate.Error is not null))
            {
                if (!Publish(RequireRestart))
                {
                    return false;
                }
            }
            if (candidate.Error is { } failure)
            {
                throw new IOException(failure);
            }
            var compiled = candidate.Compilation!;
            if (compiled.Story is not { } story)
            {
                if (configurationChanged && !RestartRequired && !Publish(RequireRestart))
                {
                    return false;
                }
                Publish(() =>
                {
                    Diagnostics = compiled.Diagnostics;
                    SourceRejected = true;
                    Error = "Story has compiler errors. Fix and save, or select Rescan / retry watcher. Any previous run is unchanged.";
                    RequestedSceneNotice = _requestedScene is { } requested
                        ? $"Requested scene '{requested}' could not be verified because the story has compiler errors."
                        : null;
                });
                return false;
            }
            if (!restart && RestartRequired)
            {
                Publish(() =>
                {
                    Diagnostics = compiled.Diagnostics;
                    Error = "Source compiled, but explicit Restart from source is required. The last good run is unchanged.";
                });
                return false;
            }
            if (!restart && _acceptedSource is { } accepted && candidate.Source is { } same
                && accepted.Files.Count == same.Files.Count
                && accepted.ImagePaths.SetEquals(same.ImagePaths)
                && accepted.Files.All(file => same.Files.TryGetValue(file.Key, out var text) && text == file.Value))
            {
                return Publish(() =>
                {
                    AdoptImages(same);
                    _acceptedSource = same;
                    Diagnostics = compiled.Diagnostics;
                    SourceRejected = false;
                    Error = null;
                });
            }
            if (!restart && Story is not null)
            {
                var historyChanged = false;
                var session = Session is { } activeSession
                    ? activeSession.Reload(story, out historyChanged)
                    : null;
                if (configurationChanged && !historyChanged)
                {
                    Publish(() =>
                    {
                        RequireRestart();
                        Diagnostics = compiled.Diagnostics;
                        Error = "Source compiled, but explicit Restart from source is required. The last good run is unchanged.";
                    });
                    return false;
                }
                var (updatedNumbers, updatedFlags, updatedTexts, disabledOverrides) = SynchronizeSetup(story);
                var updatedSelection = Character is null ? null : RevalidateCharacter(story, ValidatedCreationSeed());
                var inspection = session?.Inspect();
                return Publish(() =>
                {
                    Diagnostics = compiled.Diagnostics;
                    Story = story;
                    Numbers = updatedNumbers;
                    Flags = updatedFlags;
                    Texts = updatedTexts;
                    Character = updatedSelection;
                    Session = session;
                    Inspection = inspection;
                    _acceptedSource = candidate.Source;
                    AdoptImages(candidate.Source!);
                    AcceptedSourceMap = compiled.SourceMap;
                    SourceRejected = false;
                    RestartRequired = false;
                    Error = null;
                    RequestedSceneNotice = null;
                    ReloadStatus = session is null ? "Talemark source adopted. Ready for scene setup."
                        : historyChanged ? "History changed; continuing from here with current state."
                        : $"Reload boundary: {session.Current.Scene}. Verified command prefix retained; current passage replaced, decisions rebuilt.";
                    ReloadStatus = WithDisabledOverrides(ReloadStatus, disabledOverrides);
                    RefreshEdits();
                });
            }
            var (numbers, flags, texts, missing) = SynchronizeSetup(story);
            BuildSetup(numbers, flags, texts).Validate(story);
            var seed = Story is null ? story.Seed?.ToString(CultureInfo.InvariantCulture) ?? "" : Seed;
            var resolvedSeed = DeterministicDice.ResolveSeed(ParseSeed(seed), story.Seed, _randomSeed);
            var selection = RevalidateCharacter(story, resolvedSeed);
            var requestedScene = _requestedScene;
            var target = requestedScene ?? (string.IsNullOrWhiteSpace(Target) ? story.Start.Value : Target);
            var requestedSceneExists = requestedScene is null ||
                story.Scenes.Any(scene => scene.Id.Value == requestedScene);
            if (!requestedSceneExists)
            {
                target = story.Start.Value;
            }
            story.GetScene(new(target));
            return Publish(() =>
            {
                Diagnostics = compiled.Diagnostics;
                Story = story;
                Numbers = numbers;
                Flags = flags;
                Texts = texts;
                Target = target;
                Seed = seed;
                if (string.IsNullOrEmpty(ScenarioPath))
                {
                    ScenarioPath = Path.Combine(AuthorScenarioFiles.DefaultDirectory(story.Id), "scenario.twscenario");
                }
                Character = selection;
                ++CreationRevision;
                Session = null;
                Inspection = null;
                RefreshEdits();
                SourceRejected = false;
                RestartRequired = false;
                _requestedScene = null;
                RequestedSceneNotice = requestedSceneExists ? null
                    : $"Requested scene '{requestedScene}' was not found. The configured start scene is selected instead.";
                _acceptedSource = candidate.Source;
                AdoptImages(candidate.Source!);
                AcceptedSourceMap = compiled.SourceMap;
                Error = null;
                ReloadStatus = WithDisabledOverrides(
                    "Source restarted. Compatible setup retained and revalidated; select Start selected scene for a fresh run.", missing);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException or ArithmeticException)
        {
            Publish(() =>
            {
                if (candidate.Compilation is { } compilation)
                {
                    Diagnostics = compilation.Diagnostics;
                }
                SourceRejected = true;
                if (!restart && (ex is AuthorReloadException or KeyNotFoundException or ArgumentException))
                {
                    RestartRequired = true;
                }
                Error = $"Could not reload story: {ex.Message}. Any previous run is unchanged.";
            });
            return false;
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> Configuration(StorySource source) =>
        source.Files.Where(file => StorySource.IsConfigurationFile(file.Key)).OrderBy(file => file.Key, StringComparer.Ordinal);
    private void AdoptImages(StorySource source)
    {
        var previous = Images;
        Images = StoryImages.FromDirectory(source, Root);
        previous?.Dispose();
        if (previous is not null) _retiredImages.Add(previous);
    }

    public void Dispose() => Images?.Dispose();

    public async Task DrainImagesAsync()
    {
        var retired = _retiredImages.ToArray();
        _retiredImages.Clear();
        foreach (var images in retired) await images.DisposeAsync();
    }

    private static (SetupNumberKind Kind, string Id, string? Character) FieldKey(SetupNumber field) =>
        (field.Kind, field.Id, field.Character);

    private (IReadOnlyList<SetupNumber> Numbers, IReadOnlyList<SetupFlag> Flags,
        IReadOnlyList<SetupText> Texts, string[] Missing) SynchronizeSetup(StoryDefinition story)
    {
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
        var flags = story.World.Flags.Select(id => new SetupFlag(id, story.World.InitialFlags[id])).ToArray();
        var texts = story.World.TextVariables.Select(pair => new SetupText(pair.Key, pair.Value)).ToArray();
        var newKeys = numbers.Select(FieldKey).ToHashSet();
        var newFlagIds = flags.Select(flag => flag.Id).ToHashSet();
        var newTextIds = texts.Select(field => field.Id).ToHashSet();
        var oldNumbers = Numbers.ToDictionary(FieldKey);
        var oldFlags = Flags.ToDictionary(flag => flag.Id);
        var oldTexts = Texts.ToDictionary(field => field.Id);
        var missing = Numbers.Where(old => old.Enabled && !newKeys.Contains(FieldKey(old)))
            .Select(field => field.Label).Concat(Flags.Where(old => old.Enabled && !newFlagIds.Contains(old.Id))
                .Select(field => $"flag:{field.Id}"))
            .Concat(Texts.Where(old => old.Enabled && !newTextIds.Contains(old.Id)).Select(field => field.Label)).ToArray();
        foreach (var field in numbers)
        {
            if (oldNumbers.TryGetValue(FieldKey(field), out var old) && old.Enabled)
            {
                field.Enabled = true;
                field.Value = old.Value;
            }
        }
        foreach (var flag in flags)
        {
            if (oldFlags.TryGetValue(flag.Id, out var old) && old.Enabled)
            {
                flag.Enabled = true;
                flag.Value = old.Value;
            }
        }
        foreach (var field in texts)
        {
            if (oldTexts.TryGetValue(field.Id, out var old) && old.Enabled)
            {
                field.Enabled = true;
                field.Value = old.Value;
            }
        }
        return (numbers.AsReadOnly(), Array.AsReadOnly(flags), Array.AsReadOnly(texts), missing);
    }

    private static string WithDisabledOverrides(string status, string[] missing) =>
        missing.Length == 0 ? status : $"{status} Disabled removed or incompatible setup overrides: {string.Join(", ", missing)}.";

    private CharacterSelection? RevalidateCharacter(StoryDefinition story, ulong seed)
    {
        if (Character is null)
        {
            return null;
        }
        var invalid = Character.Stats.Where(stat => story.Creation is not { } creation
                || !creation.Stats.TryGetValue(new(stat.Key), out var values) || !values.Contains(stat.Value))
            .Select(stat => $"creation stat:{stat.Key}={stat.Value}").ToList();
        if (Character.Preset is { } preset && !(story.Creation?.Presets.Any(value => value.Id == preset) ?? false))
        {
            invalid.Add($"creation preset:{preset}");
        }
        if (invalid.Count > 0)
        {
            throw new ArgumentException($"Character selection IDs/options changed: {string.Join(", ", invalid)}. Clear character selection before restarting");
        }
        return CharacterSelection.Validate(story, Character, seed);
    }

    public IEnumerable<SceneDefinition> FindScenes(string query) => Story?.Scenes.Where(scene =>
        scene.Id.Value.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (scene.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) ?? [];

    public void SelectCharacter(CharacterSelection character) => Attempt(() => Character = CharacterSelection.Validate(
        Story ?? throw new InvalidOperationException("Load a story first."), character,
        ValidatedCreationSeed()));

    public void ClearCharacter()
    {
        Character = null;
        ++CreationRevision;
    }

    public void Start()
    {
        Attempt(() =>
        {
            var story = Story ?? throw new InvalidOperationException("Load a valid story first.");
            var candidate = AuthorSession.Create(story, new SceneId(Target), captureDecisions: true,
                BuildSetup(Numbers, Flags, Texts),
                story.Creation is null ? ParseSeed(Seed) : ValidatedCreationSeed(), Character);
            var inspection = candidate.Inspect();
            Session = candidate;
            Inspection = inspection;
            RefreshEdits();
        });
    }

    private ulong ValidatedCreationSeed() =>
        DeterministicDice.ResolveSeed(ParseSeed(Seed), Story?.Seed, _randomSeed);

    private static ulong? ParseSeed(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seed)
            ? seed : throw new ArgumentException("Seed must be an unsigned 64-bit integer, or empty for the story/random default.");
    }

    private IGameStateView StartingState()
    {
        var story = Story ?? throw new InvalidOperationException("Load a story first.");
        var state = GameState.CreateInitial(story.World, CreationSeed);

        void Apply(IEnumerable<GameEvent> events)
        {
            foreach (var gameEvent in events)
                state.Apply(gameEvent);
        }

        if (Character is { } character)
        {
            var player = story.World.Characters[EntityId.Player];
            Apply([new CharacterIdentityChanged(EntityId.Player,
                character.FirstName ?? player.FirstName,
                character.LastName ?? player.LastName,
                character.Pronouns is null ? player.Pronouns : Pronouns.FromKeyword(character.Pronouns)!)]);

            var stats = new Dictionary<StatId, int>();
            if (story.Creation!.Presets.FirstOrDefault(preset => preset.Id == character.Preset) is { } preset)
            {
                foreach (var (stat, value) in preset.Stats)
                    stats[stat] = value;
            }
            foreach (var (stat, value) in character.Stats)
                stats[new StatId(stat)] = value;
            foreach (var (stat, value) in stats.OrderBy(pair => pair.Key.Value, StringComparer.Ordinal))
                Apply(StateChanges.SetStat(state, EntityId.Player, stat, value));
        }

        var setup = BuildSetup(Numbers, Flags, Texts).Validate(story);
        foreach (var value in setup.Stats)
            Apply(StateChanges.SetStat(state, value.Character, value.Stat, value.Value));
        foreach (var value in setup.Inventory)
        {
            var current = state.GetItemCount(value.Character, value.Item);
            Apply(value.Quantity >= current
                ? StateChanges.GiveItem(value.Character, value.Item, value.Quantity - current)
                : StateChanges.TakeItem(state, value.Character, value.Item, current - value.Quantity));
        }
        foreach (var (id, value) in setup.Standings)
            Apply(StateChanges.SetStanding(state, id, value));
        foreach (var (id, value) in setup.Flags)
            Apply(StateChanges.SetFlag(state, id, value));
        foreach (var (id, value) in setup.Variables)
            Apply(StateChanges.SetVariable(state, id, value));
        foreach (var (id, value) in setup.TextVariables)
            Apply(StateChanges.SetTextVariable(state, id, value));

        return state;
    }

    private static AuthorSetup BuildSetup(IEnumerable<SetupNumber> numbers, IEnumerable<SetupFlag> flags,
        IEnumerable<SetupText> texts)
    {
        var variables = new Dictionary<VarId, int>();
        var standings = new Dictionary<FactionId, int>();
        var stats = new List<AuthorStatValue>();
        var inventory = new List<AuthorItemValue>();
        foreach (var field in numbers.Where(field => field.Enabled))
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
        return new AuthorSetup
        {
            Flags = flags.Where(field => field.Enabled).ToDictionary(field => field.Id, field => field.Value),
            Variables = variables,
            TextVariables = texts.Where(field => field.Enabled).ToDictionary(field => field.Id, field => field.Value),
            Standings = standings,
            Stats = stats,
            Inventory = inventory,
        };
    }

    public void Choose(string key) => RunCommand(session => session.Choose(key));
    public int PendingEdits =>
        EditNumbers.Count(number => number.Enabled) + EditFlags.Count(flag => flag.Enabled) + EditTexts.Count(text => text.Enabled);

    public void RevertEdits() => RefreshEdits();

    public void ApplyEdits() => RunCommand(session => session.Edit(BuildSetup(EditNumbers, EditFlags, EditTexts)));
    public void JumpPreservingState() => RunCommand(session => session.Jump(new SceneId(Target)));
    public void Back() => RunCommand(session => session.Back());
    public void Forward() => RunCommand(session => session.Forward());

    private void RunCommand(Action<AuthorSession> command) => Attempt(() =>
    {
        var session = Session ?? throw new InvalidOperationException("Start a scene first.");
        command(session);
        Inspection = session.Inspect();
        RefreshEdits();
    });

    private void RefreshEdits()
    {
        if (Session is not { } session)
        {
            EditNumbers = [];
            EditFlags = [];
            EditTexts = [];
            return;
        }
        var state = session.State;
        EditNumbers = Numbers.Select(field => new SetupNumber(field.Kind, field.Id, field.Label, field.Kind switch
        {
            SetupNumberKind.Variable => state.GetVariable(new(field.Id)),
            SetupNumberKind.Standing => state.GetStanding(new(field.Id)),
            SetupNumberKind.Stat => state.GetBaseStat(new(field.Character!), new(field.Id)),
            SetupNumberKind.Inventory => state.GetItemCount(new(field.Character!), new(field.Id)),
            _ => throw new InvalidOperationException($"Unsupported edit field {field.Kind}."),
        }, field.Character)).ToArray();
        EditFlags = Flags.Select(flag => new SetupFlag(flag.Id, state.GetFlag(flag.Id))).ToArray();
        EditTexts = Texts.Select(field => new SetupText(field.Id, state.GetTextVariable(field.Id))).ToArray();
    }

    private void Attempt(Action action)
    {
        try
        {
            action();
            if (!SourceRejected)
            {
                Error = null;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException
            or NotSupportedException or ArithmeticException)
        {
            Error = $"Playtest failed: {ex.Message}";
        }
    }
}
