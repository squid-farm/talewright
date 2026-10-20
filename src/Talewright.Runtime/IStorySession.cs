using Talewright.Core.Events;
using Talewright.Core.State;
using Talewright.Tale.Model;

namespace Talewright.Runtime;

/// <summary>
/// A single playthrough of a story. This is the seam between the engine and any presentation layer: call
/// <see cref="Start"/>, show the returned <see cref="StoryFrame"/>, and call <see cref="Choose"/> with the key of
/// whatever the player picked.
/// </summary>
public interface IStorySession
{
    /// <summary>The compiled story being played.</summary>
    StoryDefinition Story { get; }

    /// <summary>Seed for all dice in this playthrough. Same seed + same choices = same playthrough.</summary>
    ulong Seed { get; }

    /// <summary>Read-only view of the current game state (for character sheets, debug panels…).</summary>
    IGameStateView State { get; }

    /// <summary>Every event so far; the complete, replayable history of the playthrough.</summary>
    IReadOnlyList<GameEvent> Events { get; }

    /// <summary>The most recent frame, or null before <see cref="Start"/>.</summary>
    StoryFrame? Current { get; }

    /// <summary>Begins the story at its start scene.</summary>
    StoryFrame Start();

    /// <summary>Makes a choice from <see cref="Current"/>.</summary>
    /// <exception cref="InvalidOperationException">The key is not one of the current choices.</exception>
    StoryFrame Choose(string key);
}
