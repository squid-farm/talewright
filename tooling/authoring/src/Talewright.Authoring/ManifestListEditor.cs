using Talewright.Core.Identity;
using Talewright.Talemark.Analysis;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;
using Talewright.Talemark.Vocabulary;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Talewright.Authoring;

internal sealed record ManifestTextEdit(SourceRange Range, string NewText);
internal sealed record ManifestEditResult(ManifestTextEdit? Edit, string? Reason, bool SuppressAction = false);

internal static class ManifestListEditor
{
    internal static ManifestEditResult Append(string text, string file, string section, string item)
    {
        if (!TryLoadRoot(text, file, out var root, out var reason))
            return new(null, reason);
        var scalar = FormatScalar(item);
        if (root.Style == MappingStyle.Flow)
            return new(null, $"Cannot safely edit '{file}' because its root mapping uses flow style.");

        if (!root.Children.TryGetValue(new YamlScalarNode(section), out var node))
        {
            if (HasDocumentEndMarker(text))
                return new(null, $"Cannot safely add '{section}' to '{file}' because it has an explicit YAML document end marker.");

            var newline = DetectNewline(text);
            var hasFinalNewline = EndsInNewline(text);
            var separator = text.Length == 0 ? "" : hasFinalNewline ? newline : newline + newline;
            var ending = hasFinalNewline ? newline : "";
            var insertion = $"{separator}{section}:{newline}  - {scalar}{ending}";
            var point = LocationAt(file, text, text.Length);
            return new(new(new(point, point), insertion), null);
        }

        if (node is not YamlSequenceNode sequence)
            return new(null, $"Cannot safely edit '{file}' because '{section}' is not a YAML list.");

        foreach (var child in sequence.Children)
        {
            if (child is not YamlScalarNode entry ||
                entry.Style is not (ScalarStyle.Plain or ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted) ||
                !IdentifierRules.IsValid(entry.Value ?? ""))
                return new(null, $"Cannot safely edit '{file}' because '{section}' contains an unsupported or invalid entry.");
        }
        if (sequence.Children.OfType<YamlScalarNode>().Any(entry => entry.Value == item))
            return new(null, $"Cannot safely edit '{file}' because '{item}' is already declared.");

        return sequence.Style switch
        {
            SequenceStyle.Flow => AppendFlow(text, file, section, sequence, scalar),
            SequenceStyle.Block => AppendBlock(text, file, section, sequence, scalar),
            _ => new(null, $"Cannot safely edit '{file}' because '{section}' uses an unsupported YAML list style.")
        };
    }

    internal static ManifestEditResult AppendMappingEntry(
        string text, string file, string section, string key, string blockValue, string flowValue)
    {
        if (!TryLoadRoot(text, file, out var root, out var reason))
            return new(null, reason);
        if (root.Style == MappingStyle.Flow)
            return new(null, $"Cannot safely edit '{file}' because its root mapping uses flow style.");
        if (!IdentifierRules.IsValid(key))
            return new(null, $"Cannot safely add '{key}' to '{file}' because it is not a valid identifier.");

        var formattedKey = FormatScalar(key);
        if (!root.Children.TryGetValue(new YamlScalarNode(section), out var node))
        {
            if (HasDocumentEndMarker(text))
                return new(null, $"Cannot safely add '{section}' to '{file}' because it has an explicit YAML document end marker.");

            var newline = DetectNewline(text);
            var hasFinalNewline = EndsInNewline(text);
            var separator = text.Length == 0 ? "" : hasFinalNewline ? newline : newline + newline;
            var sectionEnding = hasFinalNewline ? newline : "";
            var rootIndent = root.Children.Count == 0 ? 0 : checked((int)root.Children.First().Key.Start.Column - 1);
            var entry = BlockEntry(formattedKey, blockValue, rootIndent + 2, newline);
            var insertion = $"{separator}{new string(' ', rootIndent)}{FormatScalar(section)}:{newline}{entry}{sectionEnding}";
            return CreateValidatedMappingEdit(
                text, file, root, section, key, BlockEntry(formattedKey, blockValue, 0, newline),
                text.Length, insertion);
        }

        if (node is not YamlMappingNode mapping)
            return new(null, $"Cannot safely edit '{file}' because '{section}' is not a YAML mapping.");
        if (!HasSupportedMappingKeys(mapping))
            return new(null, $"Cannot safely edit '{file}' because '{section}' contains an unsupported or invalid entry.");
        if (mapping.Children.Keys.OfType<YamlScalarNode>().Any(existing => existing.Value == key))
            return new(null, $"Cannot safely edit '{file}' because '{key}' is already declared.");

        if (!TryCreateMappingInsertion(text, file, section, mapping, key, blockValue, flowValue,
                out var mappingInsertion, out var insertionReason))
            return new(null, insertionReason);
        return CreateValidatedMappingEdit(
            text, file, root, section, key, mappingInsertion.ExpectedEntry,
            mappingInsertion.Offset, mappingInsertion.Text);
    }

    internal static ManifestEditResult AppendStoryStat(string text, string file, string id, string name)
    {
        if (!IdentifierRules.IsValid(id))
            return new(null, $"Cannot safely add stat '{id}' to '{file}' because it is not a valid identifier.");
        if (!TryLoadRoot(text, file, out var root, out var reason))
            return new(null, reason);
        if (!TryFindEntry(root, new YamlScalarNode(ManifestVocabulary.Game), out var gameNode) ||
            gameNode is not YamlMappingNode game)
            return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Game}' must be a mapping.");

        var formattedId = FormatScalar(id);
        var formattedName = FormatScalar(name);
        var flowStat = FormatStatFlowEntry(formattedId, formattedName);
        var newline = DetectNewline(text);

        if (!TryFindEntry(game, new YamlScalarNode(ManifestVocabulary.Ruleset), out var rulesetNode))
        {
            var blockValue = $"\n{ManifestVocabulary.Stats}:\n{StatBlockEntry(formattedId, formattedName, 2, "\n")}";
            var flowValue = $"{{{ManifestVocabulary.Stats}: [{flowStat}]}}";
            return AppendMappingChild(text, file, root, ManifestVocabulary.Game, game,
                ManifestVocabulary.Ruleset, blockValue, flowValue, id, name);
        }

        if (rulesetNode is YamlScalarNode shorthand)
        {
            var baseId = shorthand.Value;
            if (baseId is null || !CompilerOptions.Default.Rulesets.ContainsKey(baseId))
                return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Ruleset}' uses an unsupported shorthand base.");

            var expandedFlow = $"{{{ManifestVocabulary.Base}: {FormatScalar(baseId)}, " +
                $"{ManifestVocabulary.Stats}: [{flowStat}]}}";
            if (game.Style == MappingStyle.Flow)
            {
                var start = checked((int)shorthand.Start.Index);
                var end = checked((int)shorthand.End.Index);
                var closingBrace = text.IndexOf('}', end);
                if (closingBrace < 0 || text[end..closingBrace].Contains('#'))
                    return new(null, $"Cannot safely edit '{file}' because the shorthand '{ManifestVocabulary.Ruleset}' has an unsupported flow comment.");
                return CreateValidatedStatEdit(text, file, root, start, end - start, expandedFlow, id, name);
            }

            var key = game.Children.Keys.FirstOrDefault(candidate =>
                candidate is YamlScalarNode { Value: ManifestVocabulary.Ruleset });
            if (key is null)
                return new(null, $"Cannot safely edit '{file}' because the '{ManifestVocabulary.Ruleset}' source range is unavailable.");
            var startOffset = checked((int)key.End.Index);
            var scalarEnd = checked((int)shorthand.End.Index);
            var lineEnd = text.IndexOfAny(['\r', '\n'], scalarEnd);
            if (lineEnd < 0) lineEnd = text.Length;
            var suffix = text[scalarEnd..lineEnd];
            if (suffix.Trim() is { Length: > 0 } trailing && !trailing.TrimStart().StartsWith('#'))
                return new(null, $"Cannot safely edit '{file}' because the shorthand '{ManifestVocabulary.Ruleset}' has unsupported trailing content.");
            var endOffset = lineEnd;
            var indent = checked((int)key.Start.Column - 1);
            var expandedBlock = $":{newline}{new string(' ', indent + 2)}{ManifestVocabulary.Base}: {FormatScalar(baseId)}" +
                $"{suffix}{newline}{new string(' ', indent + 2)}{ManifestVocabulary.Stats}:" +
                $"{newline}{StatBlockEntry(formattedId, formattedName, indent + 4, newline)}";
            return CreateValidatedStatEdit(text, file, root, startOffset, endOffset - startOffset, expandedBlock, id, name);
        }

        if (rulesetNode is not YamlMappingNode ruleset)
            return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Ruleset}' is not a mapping.");
        if (IsDisabledStat(ruleset, id))
            return new(null, null, SuppressAction: true);
        if (!HasSupportedMappingKeys(ruleset) ||
            ruleset.Children.Keys.OfType<YamlScalarNode>().Any(key =>
                key.Value is not (ManifestVocabulary.Base or ManifestVocabulary.Disable or ManifestVocabulary.Stats)))
            return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Ruleset}' contains an unsupported entry.");

        if (TryFindEntry(ruleset, new YamlScalarNode(ManifestVocabulary.Stats), out var statsNode))
        {
            if (statsNode is not YamlSequenceNode stats)
                return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Stats}' is not a list.");
            if (!HasSupportedStatEntries(stats, out var existingIds))
                return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Stats}' contains an unsupported or invalid entry.");
            if (existingIds.Contains(id))
                return new(null, $"Cannot safely edit '{file}' because stat '{id}' is already declared.");

            return stats.Style switch
            {
                SequenceStyle.Flow => AppendFlowStat(text, file, root, stats, flowStat, id, name),
                SequenceStyle.Block => AppendBlockStat(text, file, root, stats, formattedId, formattedName, id, name),
                _ => new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Stats}' uses an unsupported YAML list style.")
            };
        }

        var newStatsBlock = $"\n{StatBlockEntry(formattedId, formattedName, 2, "\n")}";
        var newStatsFlow = $"[{flowStat}]";
        return AppendMappingChild(text, file, root, ManifestVocabulary.Ruleset, ruleset,
            ManifestVocabulary.Stats, newStatsBlock, newStatsFlow, id, name);
    }

    internal static ManifestEditResult SetRulesetBase(string text, string file, string baseId, string statId)
    {
        if (!TryLoadRoot(text, file, out var root, out var reason))
            return new(null, reason);
        if (root.Style == MappingStyle.Flow)
            return new(null, $"Cannot safely edit '{file}' because its root mapping uses flow style.");
        if (!TryFindEntry(root, new YamlScalarNode(ManifestVocabulary.Game), out var gameNode) ||
            gameNode is not YamlMappingNode game)
            return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Game}' must be a mapping.");

        var rulesetKey = new YamlScalarNode(ManifestVocabulary.Ruleset);
        if (!TryFindEntry(game, rulesetKey, out var rulesetNode))
            return AppendMappingEntry(text, file, ManifestVocabulary.Game, ManifestVocabulary.Ruleset,
                FormatScalar(baseId), FormatScalar(baseId));
        if (rulesetNode is not YamlMappingNode ruleset)
            return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Ruleset}' must be a mapping.");
        if (!HasSupportedMappingKeys(ruleset) ||
            ruleset.Children.Keys.OfType<YamlScalarNode>().Any(key =>
                key.Value is not (ManifestVocabulary.Base or ManifestVocabulary.Disable or ManifestVocabulary.Stats)))
            return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Ruleset}' contains an unsupported entry.");

        if (TryFindEntry(ruleset, new YamlScalarNode(ManifestVocabulary.Disable), out var disableNode))
        {
            if (disableNode is not YamlSequenceNode disabled ||
                disabled.Children.Any(entry =>
                    entry is not YamlScalarNode scalar ||
                    scalar.Style is not (ScalarStyle.Plain or ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted) ||
                    !IdentifierRules.IsValid(scalar.Value ?? "")))
                return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Disable}' is not a supported list of stat IDs.");
            if (disabled.Children.OfType<YamlScalarNode>().Any(entry => entry.Value == statId))
                return new(null, null, SuppressAction: true);
        }

        if (TryFindEntry(ruleset, new YamlScalarNode(ManifestVocabulary.Stats), out var statsNode) &&
            (statsNode is not YamlSequenceNode stats || !HasSupportedStatEntries(stats, out _)))
            return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Stats}' is not a supported list of stat definitions.");

        var baseKey = new YamlScalarNode(ManifestVocabulary.Base);
        if (!TryFindEntry(ruleset, baseKey, out var baseNode))
            return ruleset.Style == MappingStyle.Block
                ? InsertRulesetBaseAtStart(text, file, root, game, baseId)
                : AppendRulesetBase(text, file, root, ruleset, baseId);
        if (baseNode is not YamlScalarNode baseScalar)
            return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Base}' is not a scalar.");
        if (baseScalar.Value == baseId)
            return new(null, null, SuppressAction: true);
        if (baseScalar.Value != "none")
            return new(null, $"Cannot safely enable '{baseId}' because '{ManifestVocabulary.Base}' is not absent or 'none'.");
        var plainString = new YamlScalarNode("none");
        if (baseScalar.Style is not (ScalarStyle.Plain or ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted) ||
            !Equals(baseScalar.Anchor, plainString.Anchor) || !Equals(baseScalar.Tag, plainString.Tag))
            return new(null, $"Cannot safely edit '{file}' because '{ManifestVocabulary.Base}' uses an unsupported scalar style, tag, or anchor.");

        var start = checked((int)baseScalar.Start.Index);
        var end = checked((int)baseScalar.End.Index);
        return CreateValidatedEdit(text, file, root, start, end - start, FormatScalar(baseId),
            editedRoot => MatchesAfterSettingRulesetBase(root, editedRoot, baseId));
    }

    private static ManifestEditResult AppendRulesetBase(
        string text, string file, YamlMappingNode root, YamlMappingNode ruleset, string baseId)
    {
        if (!TryCreateMappingInsertion(text, file, ManifestVocabulary.Ruleset, ruleset,
                ManifestVocabulary.Base, FormatScalar(baseId), FormatScalar(baseId),
                out var insertion, out var reason))
            return new(null, reason);
        return CreateValidatedEdit(text, file, root, insertion.Offset, 0, insertion.Text,
            editedRoot => MatchesAfterAddingRulesetBase(root, editedRoot, baseId));
    }

    private static ManifestEditResult InsertRulesetBaseAtStart(
        string text, string file, YamlMappingNode root, YamlMappingNode game, string baseId)
    {
        var rulesetKey = game.Children.Keys.FirstOrDefault(key =>
            key is YamlScalarNode { Value: ManifestVocabulary.Ruleset });
        if (rulesetKey is not YamlScalarNode scalarKey)
            return new(null, $"Cannot safely edit '{file}' because the '{ManifestVocabulary.Ruleset}' source range is unavailable.");

        var keyEnd = checked((int)scalarKey.End.Index);
        var lineEnd = text.IndexOfAny(['\r', '\n'], keyEnd);
        if (lineEnd < 0)
            return new(null, $"Cannot safely edit '{file}' because the '{ManifestVocabulary.Ruleset}' block has no source line.");
        var suffix = text[keyEnd..lineEnd].TrimStart();
        if (!suffix.StartsWith(':') ||
            suffix[1..].Trim() is { Length: > 0 } trailing && !trailing.StartsWith('#'))
            return new(null, $"Cannot safely edit '{file}' because the '{ManifestVocabulary.Ruleset}' header has unsupported trailing content.");

        var newlineLength = text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1;
        var offset = lineEnd + newlineLength;
        var parentIndent = checked((int)scalarKey.Start.Column - 1);
        var indent = parentIndent + 2;
        var insertion = $"{new string(' ', indent)}{ManifestVocabulary.Base}: {FormatScalar(baseId)}{DetectNewline(text)}";
        return CreateValidatedEdit(text, file, root, offset, 0, insertion,
            editedRoot => MatchesAfterAddingRulesetBase(root, editedRoot, baseId));
    }

    private static bool IsDisabledStat(YamlMappingNode ruleset, string id) =>
        TryFindEntry(ruleset, new YamlScalarNode(ManifestVocabulary.Disable), out var disableNode) &&
        disableNode is YamlSequenceNode disabled &&
        disabled.Children.OfType<YamlScalarNode>().Any(item => item.Value == id);

    private sealed record MappingInsertion(int Offset, string Text, string ExpectedEntry);

    private static bool TryCreateMappingInsertion(
        string text, string file, string mappingName, YamlMappingNode mapping,
        string key, string blockValue, string flowValue,
        out MappingInsertion insertion, out string? reason)
    {
        insertion = null!;
        reason = null;
        var formattedKey = FormatScalar(key);
        var flowEntry = $"{formattedKey}: {flowValue}";
        if (mapping.Style == MappingStyle.Flow)
        {
            int offset;
            string textToInsert;
            if (mapping.Children.Count == 0)
            {
                offset = checked((int)mapping.Start.Index + 1);
                textToInsert = flowEntry;
            }
            else
            {
                var last = mapping.Children.Last();
                offset = checked((int)last.Value.End.Index);
                var closingBrace = text.IndexOf('}', offset);
                if (closingBrace < 0)
                {
                    reason = $"Cannot safely edit '{file}' because the '{mappingName}' source range is unavailable.";
                    return false;
                }
                if (text[offset..closingBrace].Contains('#'))
                {
                    reason = $"Cannot safely edit '{file}' because '{mappingName}' has a comment inside its flow mapping.";
                    return false;
                }
                textToInsert = $", {flowEntry}";
            }

            if (offset < 0 || offset > text.Length)
            {
                reason = $"Cannot safely edit '{file}' because the '{mappingName}' source range is unavailable.";
                return false;
            }

            insertion = new(offset, textToInsert, flowEntry);
            return true;
        }

        if (mapping.Style != MappingStyle.Block || mapping.Children.Count == 0)
        {
            reason = $"Cannot safely edit '{file}' because '{mappingName}' uses an unsupported YAML mapping style.";
            return false;
        }

        var lastEntry = mapping.Children.Last();
        var indent = checked((int)lastEntry.Key.Start.Column - 1);
        var lineEnd = OffsetAfterLine(text, checked((int)lastEntry.Value.End.Line - 1));
        if (indent < 0 || lineEnd < 0 || lineEnd > text.Length)
        {
            reason = $"Cannot safely edit '{file}' because the last '{mappingName}' entry has no source line.";
            return false;
        }

        var newline = DetectNewline(text);
        var hasFollowingContent = lineEnd < text.Length;
        var finalNewline = EndsInNewline(text);
        var prefix = lineEnd == text.Length && !finalNewline ? newline : "";
        var ending = hasFollowingContent || finalNewline ? newline : "";
        var entry = BlockEntry(formattedKey, blockValue, indent, newline);
        insertion = new(lineEnd, $"{prefix}{entry}{ending}",
            BlockEntry(formattedKey, blockValue, 0, newline));
        return true;
    }

    private static ManifestEditResult AppendMappingChild(
        string text, string file, YamlMappingNode root, string parentName, YamlMappingNode parent,
        string key, string blockValue, string flowValue, string statId, string statName)
    {
        if (!HasSupportedMappingKeys(parent))
            return new(null, $"Cannot safely edit '{file}' because '{parentName}' contains an unsupported or invalid entry.");
        if (!TryCreateMappingInsertion(text, file, parentName, parent, key, blockValue, flowValue,
                out var insertion, out var reason))
            return new(null, reason);
        return CreateValidatedEdit(text, file, root, insertion.Offset, 0, insertion.Text,
            editedRoot => MatchesAfterAddingStoryStat(root, editedRoot, statId, statName));
    }

    private static ManifestEditResult AppendFlowStat(
        string text, string file, YamlMappingNode root, YamlSequenceNode stats,
        string flowStat, string statId, string statName)
    {
        if (!TryCreateFlowSequenceInsertion(text, file, ManifestVocabulary.Stats, stats, flowStat,
                rejectComments: true, out var offset, out var insertion, out var reason))
            return new(null, reason);
        return CreateValidatedStatEdit(text, file, root, offset, 0, insertion, statId, statName);
    }

    private static ManifestEditResult AppendBlockStat(
        string text, string file, YamlMappingNode root, YamlSequenceNode stats,
        string formattedId, string formattedName, string statId, string statName)
    {
        if (stats.Children.Count == 0)
            return new(null, $"Cannot safely edit '{file}' because the empty block '{ManifestVocabulary.Stats}' list has no items.");

        var last = stats.Children[^1];
        var line = checked((int)last.Start.Line - 1);
        var lineStarts = LineStarts(text);
        if (line < 0 || line >= lineStarts.Length)
            return new(null, $"Cannot safely edit '{file}' because the last stat entry has no source line.");
        var lineStart = lineStarts[line];
        var dash = lineStart;
        while (dash < text.Length && text[dash] is ' ' or '\t') dash++;
        if (dash >= text.Length || text[dash] != '-' ||
            dash + 1 < text.Length && !char.IsWhiteSpace(text[dash + 1]))
            return new(null, $"Cannot safely edit '{file}' because the last stat entry has an unsupported layout.");

        var indent = text[lineStart..dash];
        if (last is not YamlMappingNode lastMapping || lastMapping.Children.Count == 0)
            return new(null, $"Cannot safely edit '{file}' because the last stat entry has an unsupported layout.");
        var lineEnd = OffsetAfterLine(text, checked((int)lastMapping.Children.Last().Value.End.Line - 1));
        if (lineEnd < 0 || lineEnd > text.Length)
            return new(null, $"Cannot safely edit '{file}' because the last stat entry has no source line.");
        var newline = DetectNewline(text);
        var prefix = lineEnd == text.Length && !EndsInNewline(text) ? newline : "";
        var ending = lineEnd < text.Length || EndsInNewline(text) ? newline : "";
        var entry = StatBlockEntry(formattedId, formattedName, indent.Length, newline);
        return CreateValidatedStatEdit(text, file, root, lineEnd, 0, $"{prefix}{entry}{ending}", statId, statName);
    }

    private static ManifestEditResult CreateValidatedStatEdit(
        string text, string file, YamlMappingNode originalRoot, int offset, int length,
        string insertion, string statId, string statName)
        => CreateValidatedEdit(text, file, originalRoot, offset, length, insertion,
            editedRoot => MatchesAfterAddingStoryStat(originalRoot, editedRoot, statId, statName));

    private static ManifestEditResult CreateValidatedEdit(
        string text, string file, YamlMappingNode originalRoot, int offset, int length,
        string insertion, Func<YamlMappingNode, bool> preservesData)
    {
        var reason = $"Cannot safely edit '{file}' because the change could not be verified to preserve existing manifest data.";
        if (offset < 0 || length < 0 || offset + length > text.Length)
            return new(null, reason);
        var editedText = text.Remove(offset, length).Insert(offset, insertion);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(editedText));
        }
        catch (YamlException)
        {
            return new(null, reason);
        }

        if (stream.Documents.Count != 1 ||
            stream.Documents[0].RootNode is not YamlMappingNode editedRoot ||
            !preservesData(editedRoot))
            return new(null, reason);

        var start = LocationAt(file, text, offset);
        var end = LocationAt(file, text, offset + length);
        return new(new(new(start, end), insertion), null);
    }

    private static bool MatchesAfterAddingStoryStat(
        YamlMappingNode original, YamlMappingNode edited, string statId, string statName)
    {
        var gameKey = new YamlScalarNode(ManifestVocabulary.Game);
        if (original.Children.Count != edited.Children.Count ||
            !TryFindEntry(original, gameKey, out var originalGameNode) ||
            originalGameNode is not YamlMappingNode originalGame ||
            !TryFindEntry(edited, gameKey, out var editedGameNode) ||
            editedGameNode is not YamlMappingNode editedGame ||
            !MappingsMatchExcept(original, edited, gameKey) ||
            !MappingsMatchExcept(originalGame, editedGame, new YamlScalarNode(ManifestVocabulary.Ruleset)))
            return false;

        var rulesetKey = new YamlScalarNode(ManifestVocabulary.Ruleset);
        var hasRuleset = TryFindEntry(originalGame, rulesetKey, out var originalRuleset);
        if (editedGame.Children.Count != originalGame.Children.Count + (hasRuleset ? 0 : 1) ||
            !TryFindEntry(editedGame, rulesetKey, out var editedRuleset))
            return false;

        if (!hasRuleset)
            return editedRuleset is YamlMappingNode newRuleset &&
                newRuleset.Children.Count == 1 &&
                TryFindEntry(newRuleset, new YamlScalarNode(ManifestVocabulary.Stats), out var newStats) &&
                IsSingleStat(newStats, statId, statName);

        if (originalRuleset is YamlScalarNode shorthand)
            return editedRuleset is YamlMappingNode expanded &&
                expanded.Children.Count == 2 &&
                TryFindEntry(expanded, new YamlScalarNode(ManifestVocabulary.Base), out var baseNode) &&
                baseNode is YamlScalarNode baseScalar && baseScalar.Value == shorthand.Value &&
                TryFindEntry(expanded, new YamlScalarNode(ManifestVocabulary.Stats), out var statsNode) &&
                IsSingleStat(statsNode, statId, statName);

        return originalRuleset is YamlMappingNode originalMapping &&
            editedRuleset is YamlMappingNode editedMapping &&
            MappingMatchesAfterAddingStat(originalMapping, editedMapping, statId, statName);
    }

    private static bool MappingMatchesAfterAddingStat(
        YamlMappingNode original, YamlMappingNode edited, string statId, string statName)
    {
        var statsKey = new YamlScalarNode(ManifestVocabulary.Stats);
        var hasStats = TryFindEntry(original, statsKey, out var originalStats);
        if (hasStats && originalStats is not YamlSequenceNode)
            return false;
        return edited.Children.Count == original.Children.Count + (hasStats ? 0 : 1) &&
            TryFindEntry(edited, statsKey, out var editedStats) &&
            MatchesStatsSequence(originalStats as YamlSequenceNode, editedStats, statId, statName) &&
            MappingsMatchExcept(original, edited, statsKey);
    }

    private static bool MatchesStatsSequence(
        YamlSequenceNode? original, YamlNode editedNode, string statId, string statName)
    {
        if (editedNode is not YamlSequenceNode edited || !HasSupportedStatEntries(edited, out _))
            return false;
        if (original is null)
            return IsSingleStat(edited, statId, statName);
        return edited.Style == original.Style &&
            edited.Children.Count == original.Children.Count + 1 &&
            original.Children.Zip(edited.Children).All(pair => NodesEqual(pair.First, pair.Second)) &&
            IsStatDefinition(edited.Children[^1], statId, statName);
    }

    private static bool IsSingleStat(YamlNode node, string statId, string statName) =>
        node is YamlSequenceNode { Children.Count: 1 } sequence &&
        IsStatDefinition(sequence.Children[0], statId, statName);

    private static bool IsStatDefinition(YamlNode node, string statId, string statName)
    {
        if (node is not YamlMappingNode mapping || mapping.Children.Count != 6)
            return false;
        return ScalarEquals(mapping, ManifestVocabulary.Id, statId) &&
            ScalarEquals(mapping, ManifestVocabulary.Name, statName) &&
            ScalarEquals(mapping, ManifestVocabulary.Category, ManifestVocabulary.StatCategoryCustom) &&
            ScalarEquals(mapping, ManifestVocabulary.Min, "0") &&
            ScalarEquals(mapping, ManifestVocabulary.Max, int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)) &&
            ScalarEquals(mapping, ManifestVocabulary.Default, "0");
    }

    private static bool ScalarEquals(YamlMappingNode mapping, string key, string value) =>
        TryFindEntry(mapping, new YamlScalarNode(key), out var node) &&
        node is YamlScalarNode scalar && scalar.Value == value;

    private static bool HasSupportedStatEntries(YamlSequenceNode stats, out HashSet<string> ids)
    {
        ids = new(StringComparer.Ordinal);
        var allowedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            ManifestVocabulary.Id, ManifestVocabulary.Name, ManifestVocabulary.Category,
            ManifestVocabulary.Min, ManifestVocabulary.Max, ManifestVocabulary.Default,
            ManifestVocabulary.GoverningAttribute
        };
        foreach (var child in stats.Children)
        {
            if (child is not YamlMappingNode stat ||
                !TryFindEntry(stat, new YamlScalarNode(ManifestVocabulary.Id), out var idNode) ||
                idNode is not YamlScalarNode id || !IdentifierRules.IsValid(id.Value ?? "") ||
                !HasSupportedMappingKeys(stat) ||
                stat.Children.Keys.OfType<YamlScalarNode>().Any(key => !allowedKeys.Contains(key.Value ?? "")) ||
                stat.Children.Values.Any(value => value is not YamlScalarNode) ||
                !ids.Add(id.Value!))
                return false;
        }
        return true;
    }

    private static bool MappingsMatchExcept(YamlMappingNode original, YamlMappingNode edited, YamlNode excludedKey)
    {
        foreach (var entry in original.Children)
        {
            if (NodesEqual(entry.Key, excludedKey))
                continue;
            if (!TryFindEntry(edited, entry.Key, out var value) || !NodesEqual(entry.Value, value))
                return false;
        }
        return true;
    }

    private static bool TryLoadRoot(string text, string file, out YamlMappingNode root, out string? reason)
    {
        root = null!;
        reason = null;
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException)
        {
            reason = $"Cannot safely edit '{file}' because it contains invalid YAML.";
            return false;
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
        {
            reason = $"Cannot safely edit '{file}' because it must contain one YAML mapping.";
            return false;
        }

        root = mapping;
        return true;
    }

    private static string FormatStatFlowEntry(string id, string name) =>
        $"{{{ManifestVocabulary.Id}: {id}, {ManifestVocabulary.Name}: {name}, " +
        $"{ManifestVocabulary.Category}: {ManifestVocabulary.StatCategoryCustom}, " +
        $"{ManifestVocabulary.Min}: 0, {ManifestVocabulary.Max}: {int.MaxValue}, {ManifestVocabulary.Default}: 0}}";

    private static string StatBlockEntry(string id, string name, int indent, string newline)
    {
        var prefix = new string(' ', indent);
        var child = new string(' ', indent + 2);
        return $"{prefix}- {ManifestVocabulary.Id}: {id}{newline}" +
            $"{child}{ManifestVocabulary.Name}: {name}{newline}" +
            $"{child}{ManifestVocabulary.Category}: {ManifestVocabulary.StatCategoryCustom}{newline}" +
            $"{child}{ManifestVocabulary.Min}: 0{newline}" +
            $"{child}{ManifestVocabulary.Max}: {int.MaxValue}{newline}" +
            $"{child}{ManifestVocabulary.Default}: 0";
    }

    private static ManifestEditResult CreateValidatedMappingEdit(
        string text, string file, YamlMappingNode originalRoot, string section, string key,
        string expectedEntry, int offset, string insertion)
    {
        var reason = $"Cannot safely edit '{file}' because the change could not be verified to preserve existing manifest data.";
        if (!TryParseMappingEntry(expectedEntry, out var expectedKey, out var expectedValue) ||
            !NodesEqual(expectedKey, new YamlScalarNode(key)))
            return new(null, reason);
        return CreateValidatedEdit(text, file, originalRoot, offset, 0, insertion,
            editedRoot => MatchesAfterAddingEntry(originalRoot, editedRoot, section, expectedKey, expectedValue));
    }

    private static bool TryParseMappingEntry(string text, out YamlNode key, out YamlNode value)
    {
        key = null!;
        value = null!;
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException)
        {
            return false;
        }

        if (stream.Documents.Count != 1 ||
            stream.Documents[0].RootNode is not YamlMappingNode { Children.Count: 1 } mapping)
            return false;

        var entry = mapping.Children.Single();
        key = entry.Key;
        value = entry.Value;
        return true;
    }

    private static bool MatchesAfterAddingEntry(
        YamlMappingNode original, YamlMappingNode edited, string section,
        YamlNode expectedKey, YamlNode expectedValue)
    {
        var sectionKey = new YamlScalarNode(section);
        var hasSection = TryFindEntry(original, sectionKey, out var originalSection);
        if (edited.Children.Count != original.Children.Count + (hasSection ? 0 : 1) ||
            !TryFindEntry(edited, sectionKey, out var editedSection) ||
            editedSection is not YamlMappingNode editedMapping)
            return false;

        if (hasSection)
        {
            if (originalSection is not YamlMappingNode originalMapping ||
                !MappingMatchesAfterAddingEntry(originalMapping, editedMapping, expectedKey, expectedValue))
                return false;
        }
        else if (!MappingMatchesAfterAddingEntry(
                     new YamlMappingNode(), editedMapping, expectedKey, expectedValue))
        {
            return false;
        }

        foreach (var entry in original.Children)
        {
            if (NodesEqual(entry.Key, sectionKey))
                continue;
            if (!TryFindEntry(edited, entry.Key, out var editedValue) || !NodesEqual(entry.Value, editedValue))
                return false;
        }

        return true;
    }

    private static bool MappingMatchesAfterAddingEntry(
        YamlMappingNode original, YamlMappingNode edited, YamlNode expectedKey, YamlNode expectedValue)
    {
        if (edited.Children.Count != original.Children.Count + 1 ||
            !TryFindEntry(edited, expectedKey, out var addedValue) ||
            !NodesEqual(addedValue, expectedValue))
            return false;

        foreach (var entry in original.Children)
        {
            if (!TryFindEntry(edited, entry.Key, out var editedValue) || !NodesEqual(entry.Value, editedValue))
                return false;
        }

        return true;
    }

    private static bool MatchesAfterSettingRulesetBase(
        YamlMappingNode original, YamlMappingNode edited, string baseId)
    {
        var gameKey = new YamlScalarNode(ManifestVocabulary.Game);
        var rulesetKey = new YamlScalarNode(ManifestVocabulary.Ruleset);
        var baseKey = new YamlScalarNode(ManifestVocabulary.Base);
        return original.Children.Count == edited.Children.Count &&
            TryFindEntry(original, gameKey, out var originalGameNode) &&
            originalGameNode is YamlMappingNode originalGame &&
            TryFindEntry(edited, gameKey, out var editedGameNode) &&
            editedGameNode is YamlMappingNode editedGame &&
            MappingsMatchExcept(original, edited, gameKey) &&
            originalGame.Children.Count == editedGame.Children.Count &&
            TryFindEntry(originalGame, rulesetKey, out var originalRulesetNode) &&
            originalRulesetNode is YamlMappingNode originalRuleset &&
            TryFindEntry(editedGame, rulesetKey, out var editedRulesetNode) &&
            editedRulesetNode is YamlMappingNode editedRuleset &&
            MappingsMatchExcept(originalGame, editedGame, rulesetKey) &&
            originalRuleset.Children.Count == editedRuleset.Children.Count &&
            TryFindEntry(editedRuleset, baseKey, out var editedBaseNode) &&
            editedBaseNode is YamlScalarNode { Value: var value } && value == baseId &&
            MappingsMatchExcept(originalRuleset, editedRuleset, baseKey);
    }

    private static bool MatchesAfterAddingRulesetBase(
        YamlMappingNode original, YamlMappingNode edited, string baseId)
    {
        var gameKey = new YamlScalarNode(ManifestVocabulary.Game);
        var rulesetKey = new YamlScalarNode(ManifestVocabulary.Ruleset);
        var baseKey = new YamlScalarNode(ManifestVocabulary.Base);
        return original.Children.Count == edited.Children.Count &&
            TryFindEntry(original, gameKey, out var originalGameNode) &&
            originalGameNode is YamlMappingNode originalGame &&
            TryFindEntry(edited, gameKey, out var editedGameNode) &&
            editedGameNode is YamlMappingNode editedGame &&
            MappingsMatchExcept(original, edited, gameKey) &&
            originalGame.Children.Count == editedGame.Children.Count &&
            TryFindEntry(originalGame, rulesetKey, out var originalRulesetNode) &&
            originalRulesetNode is YamlMappingNode originalRuleset &&
            TryFindEntry(editedGame, rulesetKey, out var editedRulesetNode) &&
            editedRulesetNode is YamlMappingNode editedRuleset &&
            MappingsMatchExcept(originalGame, editedGame, rulesetKey) &&
            editedRuleset.Children.Count == originalRuleset.Children.Count + 1 &&
            TryFindEntry(editedRuleset, baseKey, out var editedBaseNode) &&
            editedBaseNode is YamlScalarNode { Value: var value } && value == baseId &&
            MappingsMatchExcept(originalRuleset, editedRuleset, baseKey);
    }

    private static bool TryFindEntry(YamlMappingNode mapping, YamlNode key, out YamlNode value)
    {
        foreach (var entry in mapping.Children)
        {
            if (NodesEqual(entry.Key, key))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    private static bool NodesEqual(YamlNode left, YamlNode right)
    {
        if (left.GetType() != right.GetType() ||
            !Equals(left.Tag, right.Tag) ||
            !Equals(left.Anchor, right.Anchor))
            return false;

        return (left, right) switch
        {
            (YamlScalarNode leftScalar, YamlScalarNode rightScalar) => leftScalar.Value == rightScalar.Value,
            (YamlSequenceNode leftSequence, YamlSequenceNode rightSequence) =>
                leftSequence.Children.Count == rightSequence.Children.Count &&
                leftSequence.Children.Zip(rightSequence.Children).All(pair => NodesEqual(pair.First, pair.Second)),
            (YamlMappingNode leftMapping, YamlMappingNode rightMapping) =>
                leftMapping.Children.Count == rightMapping.Children.Count &&
                leftMapping.Children.All(entry =>
                    TryFindEntry(rightMapping, entry.Key, out var value) && NodesEqual(entry.Value, value)),
            _ => left.Equals(right)
        };
    }

    private static ManifestEditResult AppendFlow(string text, string file, string section, YamlSequenceNode sequence, string item)
    {
        if (!TryCreateFlowSequenceInsertion(text, file, section, sequence, item,
                rejectComments: false, out var offset, out var insertion, out var reason))
            return new(null, reason);
        var point = LocationAt(file, text, offset);
        return new(new(new(point, point), insertion), null);
    }

    private static bool TryCreateFlowSequenceInsertion(
        string text, string file, string section, YamlSequenceNode sequence, string item,
        bool rejectComments, out int offset, out string insertion, out string? reason)
    {
        offset = 0;
        insertion = "";
        reason = null;
        if (sequence.Children.Count == 0)
        {
            offset = checked((int)sequence.Start.Index + 1);
            insertion = item;
        }
        else
        {
            offset = checked((int)sequence.Children[^1].End.Index);
            var closingBracket = text.IndexOf(']', offset);
            if (closingBracket < 0 ||
                rejectComments && text[offset..closingBracket].Contains('#'))
            {
                reason = $"Cannot safely edit '{file}' because '{section}' source range is unavailable or contains a flow comment.";
                return false;
            }
            insertion = $", {item}";
        }

        if (offset < 0 || offset > text.Length)
        {
            reason = $"Cannot safely edit '{file}' because the '{section}' source range is unavailable.";
            return false;
        }
        return true;
    }

    private static ManifestEditResult AppendBlock(string text, string file, string section, YamlSequenceNode sequence, string item)
    {
        if (sequence.Children.Count == 0)
            return new(null, $"Cannot safely edit '{file}' because the empty '{section}' list has no block items.");

        var last = sequence.Children[^1];
        if (last.Start.Line != last.End.Line)
            return new(null, $"Cannot safely edit '{file}' because the last '{section}' item spans multiple lines.");

        var lineStarts = LineStarts(text);
        var line = checked((int)last.Start.Line - 1);
        if (line < 0 || line >= lineStarts.Length)
            return new(null, $"Cannot safely edit '{file}' because the last '{section}' item has no source line.");

        var lineStart = lineStarts[line];
        var dash = lineStart;
        while (dash < text.Length && text[dash] is ' ' or '\t') dash++;
        if (dash >= text.Length || text[dash] != '-' ||
            dash + 1 < text.Length && !char.IsWhiteSpace(text[dash + 1]))
            return new(null, $"Cannot safely edit '{file}' because the last '{section}' item has an unsupported layout.");

        var lineEnd = dash;
        while (lineEnd < text.Length && text[lineEnd] is not ('\r' or '\n')) lineEnd++;
        var childEnd = checked((int)last.End.Index);
        if (childEnd > lineEnd || text[childEnd..lineEnd].Trim() is { Length: > 0 } trailing &&
            !trailing.StartsWith('#'))
            return new(null, $"Cannot safely edit '{file}' because the last '{section}' item has an unsupported layout.");

        var newline = DetectNewline(text);
        var indentation = text[lineStart..dash];
        var hasLineEnding = lineEnd < text.Length;
        var insertionOffset = hasLineEnding
            ? lineEnd + (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n' ? 2 : 1)
            : lineEnd;
        var insertion = hasLineEnding
            ? $"{indentation}- {item}{newline}"
            : $"{newline}{indentation}- {item}";
        var point = LocationAt(file, text, insertionOffset);
        return new(new(new(point, point), insertion), null);
    }

    // YAML 1.2 core resolves these plain spellings as booleans or null.
    private static readonly HashSet<string> ReservedScalars = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null"
    };

    internal static string FormatScalar(string item) => ReservedScalars.Contains(item) ? $"'{item}'" : item;

    private static bool HasSupportedMappingKeys(YamlMappingNode mapping) =>
        mapping.Children.Keys.All(key =>
            key is YamlScalarNode scalar && IdentifierRules.IsValid(scalar.Value ?? ""));

    private static string BlockEntry(string key, string value, int indent, string newline)
    {
        var prefix = $"{new string(' ', indent)}{key}:";
        return value.StartsWith('\n')
            ? prefix + value.Replace("\n", $"{newline}{new string(' ', indent + 2)}")
            : $"{prefix} {value}";
    }

    private static int[] LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                starts.Add(index + 1);
            }
            else if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }
        return starts.ToArray();
    }

    private static int OffsetAfterLine(string text, int line)
    {
        var starts = LineStarts(text);
        if (line < 0 || line >= starts.Length) return -1;

        var end = starts[line];
        while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
        if (end < text.Length && text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n')
            return end + 2;
        return end < text.Length ? end + 1 : end;
    }

    private static SourceLocation LocationAt(string file, string text, int offset)
    {
        var line = 1;
        var column = 1;
        for (var index = 0; index < offset; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < offset && text[index + 1] == '\n') index++;
                line++;
                column = 1;
            }
            else if (text[index] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }
        return new(file, line, column);
    }

    private static bool HasDocumentEndMarker(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Any(line => line.Trim() == "...");

    private static bool EndsInNewline(string text) => text.EndsWith('\n') || text.EndsWith('\r');

    private static string DetectNewline(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
                return index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
            if (text[index] == '\n') return "\n";
        }
        return "\n";
    }
}
