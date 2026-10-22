using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.Randomness;
using Talewright.Core.State;
using Talewright.Tale.Model;

namespace Talewright.Runtime;

/// <summary>
/// Synthetic, headless scene playtesting. Successful candidates replace the active run atomically.
/// No player save format or compatibility policy is changed.
/// </summary>
public sealed class AuthorSession
{
    private StorySession _session;
    private string[] _choices;

    private AuthorSession(StoryDefinition story, SceneId target, AuthorSetup setup, ulong seed,
        CharacterSelection? character, string[] choices)
    {
        Story = story;
        StartScene = target;
        Setup = setup;
        Character = character;
        _choices = choices;
        _session = new StorySession(story, seed, character, setup);
        _session.StartAt(target);
        foreach (var choice in choices)
        {
            _session.Choose(choice);
        }
        Events = Array.AsReadOnly(_session.Events.ToArray());
        State = new AuthorStateView(EventLog.Replay(story.World, seed, Events).State);
        if (_session.EntryBoundary is { } boundary)
        {
            Checkpoint = new AuthorSceneCheckpoint(story, target, setup, seed, character,
                choices, boundary, Events);
        }
    }

    public StoryDefinition Story { get; }
    public SceneId StartScene { get; private set; }
    /// <summary>Requested setup; inspect Checkpoint.State for actual clamped values before entry.</summary>
    public AuthorSetup Setup { get; private set; }
    public CharacterSelection? Character { get; private set; }
    public bool IsSynthetic => true;
    public ulong Seed => _session.Seed;
    public IGameStateView State { get; private set; }
    public IReadOnlyList<GameEvent> Events { get; private set; }
    public StoryFrame Current => _session.Current!;
    /// <summary>Null after a transition that ends without entering another scene.</summary>
    public AuthorSceneCheckpoint? Checkpoint { get; private set; }

    public static AuthorSession Create(StoryDefinition story, SceneId target, AuthorSetup? setup = null,
        ulong? seed = null, CharacterSelection? character = null)
    {
        ArgumentNullException.ThrowIfNull(story);
        story.GetScene(target);
        var selection = CharacterSelection.Validate(story, character);
        var validated = (setup ?? new AuthorSetup()).Validate(story);
        return new AuthorSession(story, target, validated,
            seed ?? story.Seed ?? DeterministicDice.NewSeed(), selection, []);
    }

    /// <summary>Rebuilds a separate candidate; failed choices leave the active run unchanged.</summary>
    public void Choose(string key)
    {
        if (_choices.Length >= StoryHistory.MaxChoices)
        {
            throw new InvalidOperationException($"An author playthrough supports at most {StoryHistory.MaxChoices:N0} choices.");
        }
        if (!Current.Choices.Any(c => c.Key == key))
        {
            throw new InvalidOperationException($"'{key}' is not an available choice.");
        }
        var candidate = Replay(Story, StartScene, Setup, Seed, Character, [.. _choices, key]);
        Adopt(candidate);
    }

    /// <summary>Starts a fresh synthetic candidate, not a preserve-current-state jump.</summary>
    public void JumpFresh(SceneId target, AuthorSetup? setup = null, ulong? seed = null,
        CharacterSelection? character = null)
        => Adopt(Create(Story, target, setup, seed, character));

    internal static AuthorSession Replay(StoryDefinition story, SceneId target, AuthorSetup setup, ulong seed,
        CharacterSelection? character, string[] choices)
        => new(story, target, setup, seed, character, choices);

    private void Adopt(AuthorSession candidate)
    {
        _session = candidate._session;
        StartScene = candidate.StartScene;
        Setup = candidate.Setup;
        Character = candidate.Character;
        State = candidate.State;
        Events = candidate.Events;
        Checkpoint = candidate.Checkpoint;
        _choices = candidate._choices;
    }
}
