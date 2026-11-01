using Talewright.Core.Identity;
using Talewright.Narrative.Templates;
using Talewright.Talemark.Formatting;
using Talewright.Talemark.Model;

namespace Talewright.Debugger;

/// <summary>
/// Map tab data: the <see cref="StoryMap"/> projected into what the canvas draws and what the
/// accessible scene and detail controls announce. <c>map.js</c> lays out each area separately with ELK.
/// </summary>
/// <param name="Title">Story title.</param>
/// <param name="Nodes">Scenes and END in declaration order, then stubs.</param>
/// <param name="Groups">Hub-offer tag groups.</param>
/// <param name="Lines">Drawn diverts and hub offers. Duplicate diverts share one line marked ×N.</param>
/// <param name="UnreachableScenes">Scenes, including END, that the start scene can't reach.</param>
public sealed record MapView(
    string Title,
    IReadOnlyList<MapNode> Nodes,
    IReadOnlyList<MapGroup> Groups,
    IReadOnlyList<MapLine> Lines,
    int UnreachableScenes)
{
    public const string MainArea = "main";
    public const string UnreachableArea = "unreachable";
    public const string HubOfferKind = "HubOffer";

    private const int LabelConditionLength = 26;
    private const int NodeTextLength = 30;

    /// <summary>Builds the Map tab data for a successfully compiled story.</summary>
    public static MapView Build(StoryDefinition story)
    {
        ArgumentNullException.ThrowIfNull(story);
        var map = StoryMap.Build(story);
        var scenes = story.Scenes.ToDictionary(scene => scene.Id.Value, StringComparer.Ordinal);
        var areas = map.Nodes.ToDictionary(node => node.Id, node => node.IsReachable ? MainArea : UnreachableArea, StringComparer.Ordinal);
        var items = map.Nodes.ToDictionary(node => node.Id, _ => new List<MapItem>(), StringComparer.Ordinal);
        var lines = new List<MapLine>();
        var stubs = new List<MapNode>();

        // A stub stands in, inside the unreachable area, for a reachable scene that an unreachable scene leads to.
        string Endpoint(string from, string to)
        {
            if (areas[from] == areas[to])
                return to;
            var id = $"stub:{to}";
            if (stubs.All(stub => stub.Id != id))
                stubs.Add(new MapNode(id, null, UnreachableArea, [$"→ {to}"], $"Leads to {to}", null,
                    false, false, false, false, false, true, []));
            return id;
        }

        var divertLines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var divert in map.Diverts)
        {
            var from = divert.From.Value;
            var target = divert.Target.ToString();
            var to = Endpoint(from, target);
            var label = DivertLabel(divert);
            var key = $"{from}\n{to}\n{divert.Kind}\n{label}";
            var name = DivertName(divert);
            if (divertLines.TryGetValue(key, out var index))
            {
                var line = lines[index];
                lines[index] = line with { Items = [.. line.Items, name] };
            }
            else
            {
                divertLines[key] = lines.Count;
                lines.Add(new MapLine($"d{lines.Count}", from, to, areas[from], divert.Kind.ToString(),
                    divert.Outcome?.ToString(), label, [name]));
            }

            items[from].Add(new MapItem(divert.Id, lines[divertLines[key]].Id, target, name, false));
        }

        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Items.Count > 1)
                lines[i] = lines[i] with { Label = $"{lines[i].Label} ×{lines[i].Items.Count}".TrimStart() };

        // A hub-offered scene sits in the group of the tag that matched its first hub offer from its own area.
        var groupOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = new List<MapGroup>();
        foreach (var offer in map.HubOffers)
        {
            var scene = offer.Scene.Value;
            if (groupOf.ContainsKey(scene) || areas[offer.Hub.Value] != areas[scene])
                continue;
            var area = areas[scene];
            var id = $"group:{area}:{MatchedTag(offer, scenes)}";
            if (groups.All(group => group.Id != id))
                groups.Add(new MapGroup(id, area, $"#{MatchedTag(offer, scenes)}"));
            groupOf[scene] = id;
        }

        var offerLines = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var offer in map.HubOffers)
        {
            var hub = offer.Hub.Value;
            var scene = offer.Scene.Value;
            var tag = MatchedTag(offer, scenes);
            var group = $"group:{areas[hub]}:{tag}";
            var toGroup = groupOf.GetValueOrDefault(scene) == group;
            var to = toGroup ? group : Endpoint(hub, scene);
            var name = HubOfferName(offer, scenes[scene]);
            var key = $"{hub}\n{to}";
            if (offerLines.TryGetValue(key, out var index))
            {
                var line = lines[index];
                lines[index] = line with { Items = [.. line.Items, name] };
            }
            else
            {
                offerLines[key] = lines.Count;
                lines.Add(new MapLine($"o{lines.Count}", hub, to, areas[hub], HubOfferKind, null, $"offers #{tag}", [name]));
            }

            items[hub].Add(new MapItem(offer.Id, lines[offerLines[key]].Id, scene, name, true));
        }

        foreach (var index in offerLines.Values)
            if (lines[index].To.StartsWith("group:", StringComparison.Ordinal))
                lines[index] = lines[index] with { Label = $"{lines[index].Label} ({lines[index].Items.Count})" };

        var nodes = map.Nodes.Select(node => node.IsEnd
                ? new MapNode(node.Id, null, areas[node.Id], ["END"],
                    $"END{(node.IsReachable ? "" : ", unreachable")}", null, false, true, false, false,
                    node.IsReachable, false, items[node.Id])
                : SceneNode(node, scenes[node.Id], story.Start, areas[node.Id], groupOf.GetValueOrDefault(node.Id), items[node.Id]))
            .Concat(stubs)
            .ToArray();
        return new MapView(story.Title, nodes, groups, lines, map.Nodes.Count(node => !node.IsReachable));
    }

    private static MapNode SceneNode(StoryMapNode node, SceneDefinition scene, SceneId start, string area, string? group,
        IReadOnlyList<MapItem> items)
    {
        var requires = scene.Requires is null ? null : ExpressionFormatter.Format(scene.Requires);
        var text = new List<string> { node.Id, Shorten(node.Title ?? "", NodeTextLength) };
        if (requires is not null)
            text.Add($"requires {Shorten(requires, NodeTextLength)}");
        var isStart = scene.Id == start;
        var facts = new[]
        {
            isStart ? "start" : null,
            scene.Hub is null ? null : "hub",
            scene.Once ? "once" : null,
            node.IsReachable ? null : "unreachable",
            requires is null ? null : $"requires {requires}",
        }.OfType<string>();
        var name = string.Join(", ", facts.Prepend(node.Title is null ? node.Id : $"{node.Title} ({node.Id})"));
        return new MapNode(node.Id, node.Id, area, text, name, group, isStart, false, scene.Hub is not null, scene.Once,
            node.IsReachable, false, items);
    }

    private static string MatchedTag(HubOffer offer, IReadOnlyDictionary<string, SceneDefinition> scenes)
    {
        var tags = scenes[offer.Scene.Value].Tags;
        return offer.Tags.Tags.FirstOrDefault(tags.Contains) ?? offer.Tags.Tags[0];
    }

    /// <summary>The short label drawn on a divert: entry check, outcome and a shortened condition.</summary>
    public static string DivertLabel(StoryDivert divert)
    {
        var parts = new List<string>();
        if (divert.Kind == DivertKind.EntryCheck)
            parts.Add("entry");
        if (divert.Outcome is { } outcome)
            parts.Add(outcome == SkillCheckOutcome.Success ? "✓" : "✗");
        if (divert.Condition.Text.Length > 0)
            parts.Add(Shorten(divert.Condition.Text, LabelConditionLength));
        return string.Join(" ", parts);
    }

    /// <summary>The full description of one divert, used for its accessible name and hover text.</summary>
    public static string DivertName(StoryDivert divert)
    {
        var line = divert.Kind switch
        {
            DivertKind.Scene => "Divert",
            DivertKind.Choice => $"Choice {divert.ChoiceKey}{ChoiceLabel(divert)}",
            DivertKind.ChoiceSkillCheck => $"Choice skill check {divert.ChoiceKey}{ChoiceLabel(divert)}",
            DivertKind.EntryCheck => "Entry check",
            _ => throw new ArgumentOutOfRangeException(nameof(divert)),
        };
        var outcome = divert.Outcome switch
        {
            SkillCheckOutcome.Success => " on success",
            SkillCheckOutcome.Failure => " on failure",
            _ => "",
        };
        var condition = divert.Condition.Text.Length > 0 ? $", if {divert.Condition.Text}" : "";
        var start = divert.Source.Start;
        return $"{line}{outcome} to {divert.Target}{condition}, {Path.GetFileName(start.File)} line {start.Line}";
    }

    /// <summary>The full description of one hub offer, used for its accessible name and hover text.</summary>
    public static string HubOfferName(HubOffer offer, SceneDefinition scene)
    {
        var tags = string.Join(offer.Tags.MatchAll ? " and " : " or ", offer.Tags.Tags.Select(tag => $"#{tag}"));
        var title = scene.Title is null ? scene.Id.Value : $"{scene.Title} ({scene.Id})";
        var once = offer.Once ? ", once" : "";
        var condition = offer.Condition.Text.Length > 0 ? $", if {offer.Condition.Text}" : "";
        return $"Hub offer of {title} via {tags}{once}{condition}";
    }

    private static string ChoiceLabel(StoryDivert divert) =>
        divert.ChoiceLabel is null ? " (textless)" : $" “{PlainText(divert.ChoiceLabel)}”";

    private static string PlainText(IReadOnlyList<TemplateInline> inlines) => string.Concat(inlines.Select(inline => inline switch
    {
        TextTemplate text => text.Text,
        StyledTemplate styled => PlainText(styled.Children),
        LineBreakTemplate => " ",
        MediaTemplate media => media.AltText,
        SubstitutionTemplate substitution => $"{{{ExpressionFormatter.Format(substitution.Value)}}}",
        ConditionalInlineTemplate conditional => PlainText(conditional.Then),
        _ => "",
    }));

    private static string Shorten(string text, int length) =>
        text.Length <= length ? text : string.Concat(text.AsSpan(0, length - 1), "…");
}

/// <summary>A scene, END or stub on the map.</summary>
/// <param name="Id">Layout id: the scene id, <c>END</c>, or <c>stub:</c> and the scene a stub stands for.</param>
/// <param name="Scene">The scene id, or null for END and stubs.</param>
/// <param name="Area"><see cref="MapView.MainArea"/> or <see cref="MapView.UnreachableArea"/>.</param>
/// <param name="Text">Lines drawn inside the node.</param>
/// <param name="Name">Accessible name.</param>
/// <param name="Group">The hub-offer tag group containing the scene, if any.</param>
/// <param name="Start">Whether this is the start scene.</param>
/// <param name="End">Whether this is END.</param>
/// <param name="Hub">Whether the scene has <c>@hub</c>.</param>
/// <param name="Once">Whether the scene has <c>@once</c>.</param>
/// <param name="Reachable">Whether the start scene reaches it through diverts and hub offers.</param>
/// <param name="Stub">Whether this stands in for a reachable scene inside the unreachable area.</param>
/// <param name="Items">The scene's diverts and then hub offers.</param>
public sealed record MapNode(
    string Id,
    string? Scene,
    string Area,
    IReadOnlyList<string> Text,
    string Name,
    string? Group,
    bool Start,
    bool End,
    bool Hub,
    bool Once,
    bool Reachable,
    bool Stub,
    IReadOnlyList<MapItem> Items);

/// <summary>A box around the scenes offered through one tag.</summary>
public sealed record MapGroup(string Id, string Area, string Label);

/// <summary>A drawn divert or hub offer. <see cref="Items"/> holds the full description of each one it stands for.</summary>
public sealed record MapLine(
    string Id,
    string From,
    string To,
    string Area,
    string Kind,
    string? Outcome,
    string Label,
    IReadOnlyList<string> Items);

/// <summary>One divert or hub offer listed in a scene's details.</summary>
/// <param name="Id">The divert or hub offer id.</param>
/// <param name="Line">The drawn line that shows it.</param>
/// <param name="Target">The scene, or END, it leads to.</param>
/// <param name="Name">Accessible name with the full condition.</param>
/// <param name="HubOffer">Whether this is a hub offer rather than a divert.</param>
public sealed record MapItem(string Id, string Line, string Target, string Name, bool HubOffer);
