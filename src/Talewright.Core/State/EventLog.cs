using Talewright.Core.Events;
using Talewright.Core.World;

namespace Talewright.Core.State;

/// <summary>
/// The append-only event log of a playthrough plus its current projected <see cref="GameState"/>.
/// A save game is just <c>(story version, seed, Events)</c>.
/// </summary>
public sealed class EventLog
{
    private readonly List<GameEvent> _events = [];

    public EventLog(WorldDefinition world, ulong seed)
    {
        World = world;
        State = GameState.CreateInitial(world, seed);
    }

    public WorldDefinition World { get; }

    public GameState State { get; private set; }

    public IReadOnlyList<GameEvent> Events => _events;

    /// <summary>Raised after each event is applied.</summary>
    public event Action<GameEvent>? Appended;

    public void Append(GameEvent gameEvent)
    {
        State.Apply(gameEvent);
        _events.Add(gameEvent);
        Appended?.Invoke(gameEvent);
    }

    public void AppendRange(IEnumerable<GameEvent> events)
    {
        foreach (var e in events)
        {
            Append(e);
        }
    }

    /// <summary>Rebuilds a log (and its state) from a saved event list. Dice are not re-rolled.</summary>
    public static EventLog Replay(WorldDefinition world, ulong seed, IEnumerable<GameEvent> events)
    {
        var log = new EventLog(world, seed);
        foreach (var e in events)
        {
            log.State.Apply(e);
            log._events.Add(e);
        }

        return log;
    }

    /// <summary>Rewinds to the first <paramref name="count"/> events (undo / time travel).</summary>
    public void Truncate(int count)
    {
        if (count < 0 || count > _events.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var kept = _events.Take(count).ToArray();
        _events.Clear();
        State = GameState.CreateInitial(World, State.Seed);
        foreach (var e in kept)
        {
            State.Apply(e);
            _events.Add(e);
        }
    }
}
