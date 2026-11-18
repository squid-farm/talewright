using MessagePack;
using Talewright.Core.Events;
using Talewright.Core.Identity;
using Talewright.Core.State;
using Talewright.Core.World;
using Talewright.Narrative.Rendering;
using Talewright.Narrative.Templates;
using Talewright.Rules.Triggers;
using Talewright.Talemark.Model;

namespace Talewright.Runtime.Saves;

/// <summary>
/// The complete continuation state immediately before a scene (or the ending) was entered.
/// Rebuilding enters that target once with the current story, so its entry effects run exactly once.
/// </summary>
internal sealed class ContinuationCheckpoint
{
    private ContinuationCheckpoint(CheckpointPayload payload, IReadOnlyList<GameEvent> events,
        IReadOnlyList<string> triggerEdges, DivertTarget target, IReadOnlyList<RenderedBlock> pending,
        CheckpointPayload retained, EventLog eventLog, IReadOnlyList<bool?> statVisibilityBeforeEvents)
    {
        Payload = payload;
        Retained = retained;
        Events = events;
        TriggerEdges = triggerEdges;
        Target = target;
        Pending = pending;
        EventLog = eventLog;
        StatVisibilityBeforeEvents = statVisibilityBeforeEvents;
    }

    /// <summary>The checkpoint as stored, normalised to the story it was rebuilt for, including retained values.</summary>
    public CheckpointPayload Payload { get; }
    public IReadOnlyList<GameEvent> Events { get; }
    public IReadOnlyList<string> TriggerEdges { get; }
    public DivertTarget Target { get; }
    public IReadOnlyList<RenderedBlock> Pending { get; }
    private EventLog EventLog { get; }
    private IReadOnlyList<bool?> StatVisibilityBeforeEvents { get; }
    /// <summary>Saved values the story no longer declares; written back into later checkpoints.</summary>
    public CheckpointPayload Retained { get; }

    public StorySession Resume(StoryDefinition story, ulong seed, CharacterSelection? character) =>
        StorySession.Resume(story, seed, character, EventLog.Fork(), StatVisibilityBeforeEvents, TriggerEdges,
            Target, Pending);

    public static void ValidatePayload(ContinuationPayload continuation)
    {
        if (continuation.Transcript is null)
        {
            throw new InvalidDataException(SaveFormat.MalformedMessage);
        }
        ValidateCheckpoint(continuation.Current, continuation.Transcript.Count);
        if (continuation.Base is not null)
        {
            ValidateCheckpoint(continuation.Base, continuation.BaseTranscriptLength);
        }
        foreach (var frame in continuation.Transcript)
        {
            if (frame is null || frame.Blocks is null)
            {
                throw new InvalidDataException(SaveFormat.MalformedMessage);
            }
            TranscriptCodec.Validate(frame);
        }
    }

    private static void ValidateCheckpoint(CheckpointPayload? checkpoint, int precedingFrames)
    {
        if (checkpoint?.Characters is null || checkpoint.Standings is null || checkpoint.Flags is null
            || checkpoint.Variables is null || checkpoint.TextVariables is null || checkpoint.Visits is null
            || checkpoint.FiredTriggers is null || checkpoint.TriggerEdges is null || checkpoint.KnownTriggers is null
            || checkpoint.KnownItems is null || checkpoint.Pending is null)
        {
            throw new InvalidDataException(SaveFormat.MalformedMessage);
        }

        var characters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var character in checkpoint.Characters)
        {
            if (character is null || string.IsNullOrEmpty(character.Id) || !characters.Add(character.Id)
                || character.FirstName is null || character.LastName is null || character.Stats is null
                || character.Items is null)
            {
                throw new InvalidDataException(SaveFormat.MalformedMessage);
            }
            if (character.Pronouns is { } pronouns)
            {
                if (pronouns.Subject is null || pronouns.Object is null || pronouns.Possessive is null
                    || pronouns.PossessivePronoun is null || pronouns.Reflexive is null)
                {
                    throw new InvalidDataException(SaveFormat.MalformedMessage);
                }
                pronouns.ToPronouns();
            }
            ValidatePairs(character.Stats);
            ValidatePairs(character.Items, value => value > 0);
        }
        ValidatePairs(checkpoint.Standings);
        ValidatePairs(checkpoint.Flags, value => value is 0 or 1);
        ValidatePairs(checkpoint.Variables);
        ValidatePairs(checkpoint.Visits, value => value > 0);
        // Every preceding frame can contribute at most one scene entry to the pre-entry checkpoint.
        if (checkpoint.Visits.Sum(visit => (long)visit.Value) > precedingFrames)
        {
            throw new InvalidDataException(SaveFormat.MalformedMessage);
        }
        var textIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in checkpoint.TextVariables)
        {
            if (value is null || string.IsNullOrEmpty(value.Id) || value.Value is null || !textIds.Add(value.Id))
            {
                throw new InvalidDataException(SaveFormat.MalformedMessage);
            }
        }
        ValidateIds(checkpoint.FiredTriggers);
        ValidateIds(checkpoint.TriggerEdges);
        ValidateIds(checkpoint.KnownTriggers);
        ValidateIds(checkpoint.KnownItems);
        TranscriptCodec.ValidateBlocks(checkpoint.Pending);
    }

    private static void ValidatePairs(IEnumerable<NamedValuePayload> values, Func<int, bool>? valid = null)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null || string.IsNullOrEmpty(value.Id) || !ids.Add(value.Id)
                || (valid is not null && !valid(value.Value)))
            {
                throw new InvalidDataException(SaveFormat.MalformedMessage);
            }
        }
    }

    private static void ValidateIds(IEnumerable<string> values)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in values)
        {
            if (string.IsNullOrEmpty(id) || !ids.Add(id))
            {
                throw new InvalidDataException(SaveFormat.MalformedMessage);
            }
        }
    }

    public static CheckpointPayload Capture(StoryDefinition story, ulong seed, IReadOnlyList<GameEvent> events,
        PlayerEntryBoundary boundary, CheckpointPayload? retained)
    {
        var state = EventLog.Replay(story.World, seed, events.Take(boundary.EventCount)).State;
        return Capture(story, state, boundary, retained);
    }

    private static CheckpointPayload Capture(StoryDefinition story, GameState state,
        PlayerEntryBoundary boundary, CheckpointPayload? retained)
    {
        var world = story.World;
        var payload = new CheckpointPayload
        {
            Target = boundary.Target.Scene?.Value,
            CurrentScene = state.CurrentScene?.Value,
            TriggerEdges = boundary.TriggerEdges.ToList(),
            KnownTriggers = story.Triggers.Select(trigger => trigger.Id).Order(StringComparer.Ordinal).ToList(),
            KnownItems = world.Items.Keys.Select(id => id.Value).Order(StringComparer.Ordinal).ToList(),
            Pending = TranscriptCodec.Encode(boundary.Pending),
        };
        foreach (var id in world.Characters.Keys.OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            var definition = state.GetCharacterDefinition(id);
            payload.Characters.Add(new CharacterStatePayload
            {
                Id = id.Value,
                FirstName = definition.FirstName,
                LastName = definition.LastName,
                Pronouns = PronounsPayload.From(definition.Pronouns),
                Stats = world.Ruleset.Stats.OrderBy(stat => stat.Id.Value, StringComparer.Ordinal)
                    .Select(stat => new NamedValuePayload(stat.Id.Value, state.GetBaseStat(id, stat.Id))).ToList(),
                Items = state.GetInventory(id).OrderBy(item => item.Key.Value, StringComparer.Ordinal)
                    .Select(item => new NamedValuePayload(item.Key.Value, item.Value)).ToList(),
            });
        }
        payload.Standings = world.Factions.Keys.OrderBy(id => id.Value, StringComparer.Ordinal)
            .Select(id => new NamedValuePayload(id.Value, state.GetStanding(id))).ToList();
        payload.Flags = world.Flags.OrderBy(id => id.Value, StringComparer.Ordinal)
            .Select(id => new NamedValuePayload(id.Value, state.GetFlag(id) ? 1 : 0)).ToList();
        payload.Variables = world.Variables.Keys.OrderBy(id => id.Value, StringComparer.Ordinal)
            .Select(id => new NamedValuePayload(id.Value, state.GetVariable(id))).ToList();
        payload.TextVariables = world.TextVariables.Keys.OrderBy(id => id.Value, StringComparer.Ordinal)
            .Select(id => new NamedTextPayload { Id = id.Value, Value = state.GetTextVariable(id) }).ToList();
        payload.Visits = story.Scenes.Select(scene => new NamedValuePayload(scene.Id.Value, state.VisitCount(scene.Id)))
            .Where(visit => visit.Value > 0).OrderBy(visit => visit.Id, StringComparer.Ordinal).ToList();
        payload.FiredTriggers = story.Triggers.Where(trigger => state.HasFiredTrigger(trigger.Id))
            .Select(trigger => trigger.Id).Order(StringComparer.Ordinal).ToList();
        if (retained is not null)
        {
            Merge(payload, retained);
        }
        return payload;
    }

    /// <summary>
    /// Rebuilds the pre-entry state for <paramref name="story"/>. Added declarations take their declared defaults;
    /// removed declarations are retained (not loaded) and reported; a number/text variable change, a removed target
    /// scene and an invalid saved character reject the load.
    /// </summary>
    public static ContinuationCheckpoint Rebuild(StoryDefinition story, ulong seed, CharacterSelection? character,
        CheckpointPayload saved, List<string> notes)
    {
        var world = story.World;
        var initial = GameState.CreateInitial(world, seed);
        var retained = new CheckpointPayload();
        var events = new List<GameEvent>();
        var knownItems = saved.KnownItems.ToHashSet(StringComparer.Ordinal);

        DivertTarget target;
        if (saved.Target is null)
        {
            target = DivertTarget.End;
        }
        else if (story.Scenes.Any(scene => scene.Id.Value == saved.Target))
        {
            target = new DivertTarget(new SceneId(saved.Target));
        }
        else
        {
            throw Structural($"the scene '{saved.Target}' this save continues from no longer exists");
        }

        foreach (var savedCharacter in saved.Characters)
        {
            var id = new EntityId(savedCharacter.Id);
            if (!world.Characters.TryGetValue(id, out var definition))
            {
                retained.Characters.Add(savedCharacter);
                notes.Add($"Character '{id}' is no longer declared; its saved values are kept but not used.");
                continue;
            }

            var pronouns = savedCharacter.Pronouns?.ToPronouns() ?? definition.Pronouns;
            if (savedCharacter.FirstName != definition.FirstName || savedCharacter.LastName != definition.LastName
                || pronouns != definition.Pronouns)
            {
                events.Add(new CharacterIdentityChanged(id, savedCharacter.FirstName, savedCharacter.LastName, pronouns));
            }

            CharacterStatePayload? orphan = null;
            foreach (var stat in savedCharacter.Stats)
            {
                var statId = new StatId(stat.Id);
                if (!world.Ruleset.StatsById.TryGetValue(statId, out var statDefinition))
                {
                    (orphan ??= CharacterStatePayload.Empty(savedCharacter.Id)).Stats.Add(stat);
                    notes.Add($"Stat '{stat.Id}' is no longer declared; {id}'s saved value {stat.Value} is kept but not used.");
                    continue;
                }

                var value = statDefinition.Clamp(stat.Value);
                if (value != stat.Value)
                {
                    notes.Add($"{id}'s {stat.Id} {stat.Value} is outside its declared range and was clamped to {value}.");
                }
                var before = initial.GetBaseStat(id, statId);
                if (value != before)
                {
                    events.Add(new StatChanged(id, statId, before, value));
                }
            }

            var savedItems = savedCharacter.Items.ToDictionary(item => item.Id, item => item.Value, StringComparer.Ordinal);
            foreach (var item in savedCharacter.Items.Where(item => !world.Items.ContainsKey(new ItemId(item.Id))))
            {
                (orphan ??= CharacterStatePayload.Empty(savedCharacter.Id)).Items.Add(item);
                notes.Add($"Item '{item.Id}' is no longer declared; {id}'s {item.Value} saved are kept but not used.");
            }
            foreach (var item in world.Items.Keys.OrderBy(item => item.Value, StringComparer.Ordinal))
            {
                var before = initial.GetItemCount(id, item);
                var after = knownItems.Contains(item.Value) ? savedItems.GetValueOrDefault(item.Value) : before;
                if (after > before) events.Add(new ItemsGranted(id, item, after - before));
                else if (after < before) events.Add(new ItemsRemoved(id, item, before - after));
            }

            if (orphan is not null) retained.Characters.Add(orphan);
        }

        foreach (var standing in saved.Standings)
        {
            var id = new FactionId(standing.Id);
            if (!world.Factions.ContainsKey(id))
            {
                retained.Standings.Add(standing);
                notes.Add($"Faction '{standing.Id}' is no longer declared; its saved standing is kept but not used.");
            }
            else if (initial.GetStanding(id) != standing.Value)
            {
                events.Add(new StandingChanged(id, initial.GetStanding(id), standing.Value));
            }
        }

        foreach (var flag in saved.Flags)
        {
            var id = new FlagId(flag.Id);
            if (!world.Flags.Contains(id))
            {
                retained.Flags.Add(flag);
                notes.Add($"Flag '{flag.Id}' is no longer declared; its saved value is kept but not used.");
            }
            else if (initial.GetFlag(id) != (flag.Value != 0))
            {
                events.Add(new FlagChanged(id, flag.Value != 0));
            }
        }

        foreach (var variable in saved.Variables)
        {
            var id = new VarId(variable.Id);
            if (world.TextVariables.ContainsKey(id))
            {
                throw Structural($"the variable '{variable.Id}' changed from a number to text");
            }
            if (!world.Variables.ContainsKey(id))
            {
                retained.Variables.Add(variable);
                notes.Add($"Variable '{variable.Id}' is no longer declared; its saved value {variable.Value} is kept but not used.");
            }
            else if (initial.GetVariable(id) != variable.Value)
            {
                events.Add(new VariableChanged(id, initial.GetVariable(id), variable.Value));
            }
        }

        foreach (var variable in saved.TextVariables)
        {
            var id = new VarId(variable.Id);
            if (world.Variables.ContainsKey(id))
            {
                throw Structural($"the variable '{variable.Id}' changed from text to a number");
            }
            if (!world.TextVariables.ContainsKey(id))
            {
                retained.TextVariables.Add(variable);
                notes.Add($"Variable '{variable.Id}' is no longer declared; its saved text is kept but not used.");
            }
            else if (initial.GetTextVariable(id) != variable.Value)
            {
                events.Add(new TextVariableChanged(id, initial.GetTextVariable(id), variable.Value));
            }
        }

        var triggerIds = story.Triggers.Select(trigger => trigger.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var fired in saved.FiredTriggers)
        {
            if (triggerIds.Contains(fired)) events.Add(new TriggerFired(fired));
            else retained.FiredTriggers.Add(fired);
        }
        foreach (var id in saved.KnownTriggers.Where(id => !triggerIds.Contains(id)))
        {
            retained.KnownTriggers.Add(id);
            notes.Add($"Trigger '{id}' is no longer declared; its saved state is kept but not used.");
        }
        retained.TriggerEdges.AddRange(saved.TriggerEdges.Where(id => !triggerIds.Contains(id)));
        foreach (var id in saved.KnownItems.Where(id => !world.Items.ContainsKey(new ItemId(id))))
        {
            retained.KnownItems.Add(id);
            if (saved.Characters.All(character => character.Items.All(item => item.Id != id)))
            {
                notes.Add($"Item '{id}' is no longer declared; its saved count is kept but not used.");
            }
        }

        // Visit counts key deterministic dice. The scene the player chose from is entered last so it stays current.
        var sceneIds = story.Scenes.Select(scene => scene.Id.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var visit in saved.Visits.OrderBy(visit => visit.Id == saved.CurrentScene))
        {
            if (!sceneIds.Contains(visit.Id))
            {
                retained.Visits.Add(visit);
                notes.Add($"Scene '{visit.Id}' is no longer declared; its saved visits are kept but not used.");
                continue;
            }
            for (var count = 0; count < visit.Value; count++)
            {
                events.Add(new SceneEntered(new SceneId(visit.Id)));
            }
        }

        var eventLog = new EventLog(world, seed);
        var statVisibilityBeforeEvents = new List<bool?>(events.Count);
        eventLog.Appending += gameEvent =>
        {
            var stat = StatDisclosureVisibility.StatFor(gameEvent, world.Ruleset);
            statVisibilityBeforeEvents.Add(stat is { } id
                ? story.IsStatVisible(id, eventLog.State)
                : null);
        };
        eventLog.AppendRange(events);
        var state = eventLog.State;
        var known = saved.KnownTriggers.ToHashSet(StringComparer.Ordinal);
        var savedEdges = saved.TriggerEdges.ToHashSet(StringComparer.Ordinal);
        // New triggers are primed against the loaded state, so an update alone never fires one.
        var primed = story.Triggers.All(trigger => known.Contains(trigger.Id))
            ? []
            : new TriggerEngine(story.Triggers, story.Predicates, state).CaptureEdgeMemory().ToHashSet(StringComparer.Ordinal);
        var edges = story.Triggers.Select(trigger => trigger.Id)
            .Where(id => known.Contains(id) ? savedEdges.Contains(id) : primed.Contains(id))
            .Order(StringComparer.Ordinal).ToArray();

        var pending = TranscriptCodec.Decode(saved.Pending);
        var normalized = Capture(story, state, new PlayerEntryBoundary(target, events.Count, edges, pending),
            retained);
        return new ContinuationCheckpoint(normalized, events.AsReadOnly(), edges, target, pending, retained,
            eventLog, statVisibilityBeforeEvents.AsReadOnly());
    }

    private static void Merge(CheckpointPayload payload, CheckpointPayload retained)
    {
        foreach (var character in retained.Characters)
        {
            if (payload.Characters.FirstOrDefault(existing => existing.Id == character.Id) is { } current)
            {
                current.Stats.AddRange(character.Stats.Where(stat => current.Stats.All(existing => existing.Id != stat.Id)));
                current.Items.AddRange(character.Items.Where(item => current.Items.All(existing => existing.Id != item.Id)));
            }
            else
            {
                payload.Characters.Add(character);
            }
        }
        payload.Standings.AddRange(retained.Standings.Where(value => payload.Standings.All(existing => existing.Id != value.Id)));
        payload.Flags.AddRange(retained.Flags.Where(value => payload.Flags.All(existing => existing.Id != value.Id)));
        payload.Variables.AddRange(retained.Variables.Where(value => payload.Variables.All(existing => existing.Id != value.Id)));
        payload.TextVariables.AddRange(retained.TextVariables.Where(value => payload.TextVariables.All(existing => existing.Id != value.Id)));
        payload.Visits.AddRange(retained.Visits.Where(value => payload.Visits.All(existing => existing.Id != value.Id)));
        payload.FiredTriggers.AddRange(retained.FiredTriggers.Where(id => !payload.FiredTriggers.Contains(id)));
        payload.TriggerEdges.AddRange(retained.TriggerEdges.Where(id => !payload.TriggerEdges.Contains(id)));
        payload.KnownTriggers.AddRange(retained.KnownTriggers.Where(id => !payload.KnownTriggers.Contains(id)));
        payload.KnownItems.AddRange(retained.KnownItems.Where(id => !payload.KnownItems.Contains(id)));

        payload.Characters.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        foreach (var character in payload.Characters)
        {
            character.Stats.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            character.Items.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        }
        payload.Standings.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        payload.Flags.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        payload.Variables.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        payload.TextVariables.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        payload.Visits.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        payload.FiredTriggers.Sort(StringComparer.Ordinal);
        payload.TriggerEdges.Sort(StringComparer.Ordinal);
        payload.KnownTriggers.Sort(StringComparer.Ordinal);
        payload.KnownItems.Sort(StringComparer.Ordinal);
    }

    private static InvalidDataException Structural(string reason) =>
        new($"This save cannot continue in the updated story because {reason}. Open it with the story version that created it.");
}

/// <summary>Converts rendered transcript output to and from its save payload.</summary>
internal static class TranscriptCodec
{
    public static void Validate(FramePayload frame)
    {
        ValidateBlocks(frame.Blocks);
        if (frame.Chosen is not null)
        {
            ValidateInlines(frame.Chosen);
        }
    }

    public static void ValidateBlocks(IEnumerable<BlockPayload>? blocks)
    {
        if (blocks is null) throw new InvalidDataException(SaveFormat.MalformedMessage);
        foreach (var block in blocks)
        {
            if (block is null) throw new InvalidDataException(SaveFormat.MalformedMessage);
            switch (block.Kind)
            {
                case 0:
                    ValidateInlines(block.Inlines);
                    break;
                case 1:
                    break;
                case 2:
                    Required(block.Speaker);
                    Required(block.Text);
                    if (block.Paragraphs is null) throw new InvalidDataException(SaveFormat.MalformedMessage);
                    foreach (var paragraph in block.Paragraphs) ValidateInlines(paragraph);
                    break;
                case 3:
                    DecodeNoteKind(block.NoteKind);
                    Required(block.Text);
                    break;
                default:
                    throw new InvalidDataException(SaveFormat.MalformedMessage);
            }
        }
    }

    private static void ValidateInlines(IEnumerable<InlinePayload>? inlines)
    {
        if (inlines is null) throw new InvalidDataException(SaveFormat.MalformedMessage);
        foreach (var inline in inlines)
        {
            if (inline is null) throw new InvalidDataException(SaveFormat.MalformedMessage);
            switch (inline.Kind)
            {
                case 0:
                    Required(inline.Text);
                    DecodeStyle(inline.Style);
                    break;
                case 1:
                case 2:
                    break;
                case 3:
                    Required(inline.MediaId);
                    Required(inline.Text);
                    DecodeStyle(inline.Style);
                    DecodeAccessibility(inline.Accessibility);
                    break;
                default:
                    throw new InvalidDataException(SaveFormat.MalformedMessage);
            }
        }
    }

    public static List<BlockPayload> Encode(IEnumerable<RenderedBlock> blocks) => blocks.Select(block => block switch
    {
        RenderedParagraph paragraph => new BlockPayload { Kind = 0, Inlines = Encode(paragraph.Inlines) },
        RenderedBlankLine => new BlockPayload { Kind = 1 },
        RenderedSpeech speech => new BlockPayload
        {
            Kind = 2,
            Text = speech.SpeakerName,
            Speaker = speech.Speaker.Value,
            Paragraphs = speech.Paragraphs.Select(Encode).ToList(),
        },
        RenderedNote note => new BlockPayload { Kind = 3, NoteKind = EncodeNoteKind(note.Kind), Text = note.Text },
        _ => throw new NotSupportedException($"Rendered block '{block.GetType().Name}' cannot be saved."),
    }).ToList();

    public static List<InlinePayload> Encode(IEnumerable<RenderedInline> inlines) => inlines.Select(inline => inline switch
    {
        RenderedText text => new InlinePayload { Kind = 0, Text = text.Text, Style = EncodeStyle(text.Style) },
        RenderedLineBreak => new InlinePayload { Kind = 1 },
        RenderedBlankLineInline => new InlinePayload { Kind = 2 },
        RenderedMedia media => new InlinePayload
        {
            Kind = 3,
            Text = media.AltText,
            MediaId = media.MediaId,
            Style = EncodeStyle(media.Style),
            Accessibility = EncodeAccessibility(media.Accessibility),
        },
        _ => throw new NotSupportedException($"Rendered inline '{inline.GetType().Name}' cannot be saved."),
    }).ToList();

    public static IReadOnlyList<RenderedBlock> Decode(IEnumerable<BlockPayload>? blocks) =>
        Array.AsReadOnly((blocks ?? throw new InvalidDataException(SaveFormat.MalformedMessage))
            .Select(DecodeBlock).ToArray());

    private static RenderedBlock DecodeBlock(BlockPayload? block)
    {
        if (block is null)
        {
            throw new InvalidDataException(SaveFormat.MalformedMessage);
        }
        return block.Kind switch
        {
            0 => new RenderedParagraph(Decode(block.Inlines)),
            1 => new RenderedBlankLine(),
            2 => new RenderedSpeech(new EntityId(Required(block.Speaker)), Required(block.Text),
                (block.Paragraphs ?? throw new InvalidDataException(SaveFormat.MalformedMessage))
                    .Select(Decode).ToArray()),
            3 => new RenderedNote(DecodeNoteKind(block.NoteKind), Required(block.Text)),
            _ => throw new InvalidDataException(SaveFormat.MalformedMessage),
        };
    }

    public static IReadOnlyList<RenderedInline> Decode(IEnumerable<InlinePayload>? inlines) =>
        Array.AsReadOnly((inlines ?? throw new InvalidDataException(SaveFormat.MalformedMessage))
            .Select(DecodeInline).ToArray());

    private static RenderedInline DecodeInline(InlinePayload? inline)
    {
        if (inline is null)
        {
            throw new InvalidDataException(SaveFormat.MalformedMessage);
        }
        return inline.Kind switch
        {
            0 => new RenderedText(Required(inline.Text), DecodeStyle(inline.Style)),
            1 => new RenderedLineBreak(),
            2 => new RenderedBlankLineInline(),
            3 => new RenderedMedia(Required(inline.MediaId), Required(inline.Text))
            {
                Style = DecodeStyle(inline.Style),
                Accessibility = DecodeAccessibility(inline.Accessibility),
            },
            _ => throw new InvalidDataException(SaveFormat.MalformedMessage),
        };
    }

    public static FramePayload Encode(HistoryFrame frame) => new()
    {
        Scene = frame.Frame.Scene?.Value,
        Title = frame.Frame.Title,
        Blocks = Encode(frame.Frame.Document.Blocks),
        Chosen = frame.Chosen is null ? null : Encode(frame.Chosen),
        ChosenAutomatically = frame.ChosenAutomatically,
        IsEnded = frame.Frame.IsEnded,
        ContinuesPrevious = frame.Frame.ContinuesPrevious,
        ClearsPrevious = frame.Frame.ClearsPrevious,
    };

    /// <summary>A read-only historical passage: it offers no choices and is never executed.</summary>
    public static HistoryFrame Decode(FramePayload frame) =>
        new(new StoryFrame(frame.Scene is null ? null : new SceneId(frame.Scene), frame.Title,
            new RenderedDocument(Decode(frame.Blocks)), [], frame.IsEnded)
        {
            ContinuesPrevious = frame.ContinuesPrevious,
            ClearsPrevious = frame.ClearsPrevious,
        }, frame.Chosen is null ? null : Decode(frame.Chosen))
        {
            ChosenAutomatically = frame.ChosenAutomatically,
        };

    private static string Required(string? value) => value ?? throw new InvalidDataException(SaveFormat.MalformedMessage);

    internal static int EncodeNoteKind(NoteKind kind) => kind switch
    {
        NoteKind.Check => 0,
        NoteKind.Change => 1,
        NoteKind.Info => 2,
        _ => throw new NotSupportedException($"Note kind '{kind}' has no save token."),
    };

    internal static NoteKind DecodeNoteKind(int token) => token switch
    {
        0 => NoteKind.Check,
        1 => NoteKind.Change,
        2 => NoteKind.Info,
        _ => throw new InvalidDataException(SaveFormat.MalformedMessage),
    };

    internal static int EncodeStyle(TextStyle style) => style switch
    {
        TextStyle.None => 0,
        TextStyle.Emphasis => 1,
        TextStyle.Strong => 2,
        TextStyle.Emphasis | TextStyle.Strong => 3,
        TextStyle.Code => 4,
        TextStyle.Emphasis | TextStyle.Code => 5,
        TextStyle.Strong | TextStyle.Code => 6,
        TextStyle.Emphasis | TextStyle.Strong | TextStyle.Code => 7,
        _ => throw new NotSupportedException($"Text style '{style}' has no save token."),
    };

    internal static TextStyle DecodeStyle(int token) => token switch
    {
        0 => TextStyle.None,
        1 => TextStyle.Emphasis,
        2 => TextStyle.Strong,
        3 => TextStyle.Emphasis | TextStyle.Strong,
        4 => TextStyle.Code,
        5 => TextStyle.Emphasis | TextStyle.Code,
        6 => TextStyle.Strong | TextStyle.Code,
        7 => TextStyle.Emphasis | TextStyle.Strong | TextStyle.Code,
        _ => throw new InvalidDataException(SaveFormat.MalformedMessage),
    };

    internal static int EncodeAccessibility(MediaAccessibility accessibility) => accessibility switch
    {
        MediaAccessibility.Descriptive => 0,
        MediaAccessibility.Decorative => 1,
        _ => throw new NotSupportedException($"Media accessibility '{accessibility}' has no save token."),
    };

    internal static MediaAccessibility DecodeAccessibility(int token) => token switch
    {
        0 => MediaAccessibility.Descriptive,
        1 => MediaAccessibility.Decorative,
        _ => throw new InvalidDataException(SaveFormat.MalformedMessage),
    };
}

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class CheckpointPayload
{
    /// <summary>Scene entered next, or nil for the ending.</summary>
    [Key(0)] public string? Target { get; set; }
    [Key(1)] public string? CurrentScene { get; set; }
    [Key(2)] public List<CharacterStatePayload> Characters { get; set; } = [];
    [Key(3)] public List<NamedValuePayload> Standings { get; set; } = [];
    [Key(4)] public List<NamedValuePayload> Flags { get; set; } = [];
    [Key(5)] public List<NamedValuePayload> Variables { get; set; } = [];
    [Key(6)] public List<NamedTextPayload> TextVariables { get; set; } = [];
    [Key(7)] public List<NamedValuePayload> Visits { get; set; } = [];
    [Key(8)] public List<string> FiredTriggers { get; set; } = [];
    [Key(9)] public List<string> TriggerEdges { get; set; } = [];
    [Key(10)] public List<string> KnownTriggers { get; set; } = [];
    [Key(11)] public List<string> KnownItems { get; set; } = [];
    [Key(12)] public List<BlockPayload> Pending { get; set; } = [];
}

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class CharacterStatePayload
{
    [Key(0)] public string Id { get; set; } = "";
    [Key(1)] public string FirstName { get; set; } = "";
    [Key(2)] public string LastName { get; set; } = "";
    [Key(3)] public PronounsPayload? Pronouns { get; set; }
    [Key(4)] public List<NamedValuePayload> Stats { get; set; } = [];
    [Key(5)] public List<NamedValuePayload> Items { get; set; } = [];

    public static CharacterStatePayload Empty(string id) => new() { Id = id };
}

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class PronounsPayload
{
    [Key(0)] public string Subject { get; set; } = "";
    [Key(1)] public string Object { get; set; } = "";
    [Key(2)] public string Possessive { get; set; } = "";
    [Key(3)] public string PossessivePronoun { get; set; } = "";
    [Key(4)] public string Reflexive { get; set; } = "";
    [Key(5)] public int Agreement { get; set; }

    public static PronounsPayload From(Pronouns pronouns) => new()
    {
        Subject = pronouns.Subject,
        Object = pronouns.Object,
        Possessive = pronouns.Possessive,
        PossessivePronoun = pronouns.PossessivePronoun,
        Reflexive = pronouns.Reflexive,
        Agreement = pronouns.Agreement switch
        {
            GrammaticalNumber.Singular => 0,
            GrammaticalNumber.Plural => 1,
            _ => throw new NotSupportedException($"Pronoun agreement '{pronouns.Agreement}' has no save token."),
        },
    };

    public Pronouns ToPronouns() =>
        new(Subject, Object, Possessive, PossessivePronoun, Reflexive, Agreement switch
        {
            0 => GrammaticalNumber.Singular,
            1 => GrammaticalNumber.Plural,
            _ => throw new InvalidDataException(SaveFormat.MalformedMessage),
        });
}

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class NamedValuePayload
{
    public NamedValuePayload()
    {
    }

    public NamedValuePayload(string id, int value)
    {
        Id = id;
        Value = value;
    }

    [Key(0)] public string Id { get; set; } = "";
    [Key(1)] public int Value { get; set; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class NamedTextPayload
{
    [Key(0)] public string Id { get; set; } = "";
    [Key(1)] public string Value { get; set; } = "";
}

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class FramePayload
{
    [Key(0)] public string? Scene { get; set; }
    [Key(1)] public string? Title { get; set; }
    [Key(2)] public List<BlockPayload> Blocks { get; set; } = [];
    [Key(3)] public List<InlinePayload>? Chosen { get; set; }
    [Key(4)] public bool ChosenAutomatically { get; set; }
    [Key(5)] public bool IsEnded { get; set; }
    [Key(6)] public bool ContinuesPrevious { get; set; }
    [Key(7)] public bool ClearsPrevious { get; set; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class BlockPayload
{
    /// <summary>0 paragraph, 1 blank line, 2 speech, 3 note.</summary>
    [Key(0)] public int Kind { get; set; }
    [Key(1)] public List<InlinePayload>? Inlines { get; set; }
    [Key(2)] public string? Speaker { get; set; }
    [Key(3)] public string? Text { get; set; }
    [Key(4)] public List<List<InlinePayload>>? Paragraphs { get; set; }
    [Key(5)] public int NoteKind { get; set; }
}

[MessagePackObject(AllowPrivate = true)]
internal sealed partial class InlinePayload
{
    /// <summary>0 text, 1 line break, 2 blank line, 3 media.</summary>
    [Key(0)] public int Kind { get; set; }
    [Key(1)] public string? Text { get; set; }
    [Key(2)] public int Style { get; set; }
    [Key(3)] public string? MediaId { get; set; }
    [Key(4)] public int Accessibility { get; set; }
}
