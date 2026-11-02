using System.Globalization;
using Talewright.Core.Identity;
using Talewright.Talemark.Diagnostics;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Talewright.Talemark.Parsing;

/// <summary>Shared source locations, scalar readers, and diagnostics for one story's YAML documents.</summary>
internal sealed class StoryYamlContext(string file, DiagnosticBag diagnostics)
{
    private readonly Dictionary<YamlNode, string> _files = new(ReferenceEqualityComparer.Instance);

    internal DiagnosticBag Diagnostics { get; } = diagnostics;

    internal SourceLocation At(YamlNode node) => new(_files.GetValueOrDefault(node, file),
        Math.Max(1, (int)node.Start.Line), Math.Max(1, (int)node.Start.Column));

    internal YamlMappingNode? Parse(string sourceFile, string text)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            Diagnostics.Error("TW4001", $"Invalid YAML: {ex.InnerException?.Message ?? ex.Message}", new SourceLocation(sourceFile, (int)ex.Start.Line, (int)ex.Start.Column));
            return null;
        }

        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            Diagnostics.Error("TW4001", "A story configuration file must contain one mapping (key: value pairs).", new SourceLocation(sourceFile, 1, 1));
            return null;
        }
        void Register(YamlNode node)
        {
            if (!_files.TryAdd(node, sourceFile)) return;
            if (node is YamlMappingNode map)
                foreach (var (key, value) in map.Children) { Register(key); Register(value); }
            else if (node is YamlSequenceNode sequence)
                foreach (var child in sequence.Children) Register(child);
        }
        Register(root);
        return root;
    }

    internal static YamlNode? Get(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    internal static string? Scalar(YamlNode? node) => (node as YamlScalarNode)?.Value;

    internal string? RequiredScalar(YamlMappingNode map, string key)
    {
        var node = Get(map, key);
        if (node is null)
        {
            Diagnostics.Error("TW4002", $"Missing required '{key}'.", At(map));
            return null;
        }

        if (Scalar(node) is not { Length: > 0 } value)
        {
            Diagnostics.Error("TW4003", $"'{key}' must be a single value.", At(node));
            return null;
        }

        return value;
    }

    internal string? RequiredIdentifier(YamlMappingNode map, string key)
    {
        var value = RequiredScalar(map, key);
        var node = Get(map, key);
        return value is null || node is null ? null : Identifier(node, $"'{key}'");
    }

    internal string? Identifier(YamlNode node, string what)
    {
        var value = Scalar(node);
        if (value is null || !IdentifierRules.IsValid(value))
        {
            var found = value is null ? "a non-string value" : $"'{value}'";
            return Error<string?>(node, "TW4003",
                $"Invalid {what} {found}. Use an ASCII letter or '_' followed by letters, digits, '_' and single '-' or '.' separators.", null);
        }

        return value;
    }

    internal int? Int(YamlNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (int.TryParse(Scalar(node), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        Diagnostics.Error("TW4003", $"Expected a whole number but found '{Scalar(node) ?? node.NodeType.ToString()}'.", At(node));
        return null;
    }

    internal YamlMappingNode? Mapping(YamlNode node, string what)
    {
        if (node is YamlMappingNode map)
        {
            return map;
        }

        Diagnostics.Error("TW4003", $"'{what}' must be a mapping (key: value pairs).", At(node));
        return null;
    }

    internal IEnumerable<YamlNode> Sequence(YamlNode node, string what)
    {
        if (node is YamlSequenceNode seq)
        {
            return seq.Children;
        }

        Diagnostics.Error("TW4003", $"'{what}' must be a list.", At(node));
        return [];
    }

    internal void CheckKeys(YamlMappingNode map, params string[] allowed)
    {
        foreach (var key in map.Children.Keys)
        {
            if (Scalar(key) is { } name && !allowed.Contains(name))
            {
                if (name == "plural" && (allowed.Contains("agreement") || allowed.Contains("name-agreement")))
                {
                    var replacement = allowed.Contains("agreement") ? "agreement" : "name-agreement";
                    Error(key, "TW4003", $"The obsolete 'plural' key is no longer supported. Use '{replacement}: singular' or '{replacement}: plural' for grammar, not population.", 0);
                    continue;
                }

                Diagnostics.Warning("TW4006", $"Unknown key '{name}' (ignored). Expected one of: {string.Join(", ", allowed)}.", At(key));
            }
        }
    }

    internal T Error<T>(YamlNode node, string code, string message, T fallback)
    {
        Diagnostics.Error(code, message, At(node));
        return fallback;
    }
}
