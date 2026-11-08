using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Parsing;

/// <summary>Finds registered stat providers only when a diagnostic needs them.</summary>
internal sealed class StatDiagnosticGuidance
{
    private readonly CompilerOptions _options;
    private readonly Lazy<Dictionary<StatId, Provider>> _providers;
    private string? _baseName;
    private Func<Ruleset>? _baseFactory;
    private Ruleset? _baseRuleset;

    internal StatDiagnosticGuidance(CompilerOptions options)
    {
        _options = options;
        _providers = new Lazy<Dictionary<StatId, Provider>>(IndexProviders);
    }

    internal void SetBase(string name, Func<Ruleset> factory, Ruleset ruleset)
    {
        _baseName = name;
        _baseFactory = factory;
        _baseRuleset = ruleset;
    }

    internal string ForMissingStat(StatId stat)
    {
        if (_baseRuleset?.HasStat(stat) == true)
        {
            return $" '{stat}' was removed from the '{_baseName}' base ruleset by " +
                $"'{ManifestVocabulary.Ruleset}.{ManifestVocabulary.Disable}' in {StorySource.ManifestFileName}. " +
                $"Remove '{stat}' from '{ManifestVocabulary.Ruleset}.{ManifestVocabulary.Disable}'. " +
                DeclareCustom(stat);
        }

        if (!_providers.Value.TryGetValue(stat, out var provider)) return "";
        if (!provider.Selectable)
        {
            return $" '{stat}' is provided by the registered ruleset '{provider.Name}', but that name cannot be " +
                $"selected in {StorySource.ManifestFileName}. Register it with a valid ruleset id in C#. " +
                DeclareCustom(stat);
        }

        var value = YamlScalar(provider.Name);
        return $" '{stat}' is provided by the '{provider.Name}' ruleset. In {StorySource.ManifestFileName}, " +
            $"set '{ManifestVocabulary.Ruleset}: {value}' under '{ManifestVocabulary.Game}:' " +
            $"(or '{ManifestVocabulary.Base}: {value}' inside an existing '{ManifestVocabulary.Ruleset}:' mapping). " +
            DeclareCustom(stat);
    }

    internal string ForInvalidDisable(StatId stat)
    {
        if (_baseRuleset?.HasStat(stat) == true || !_providers.Value.TryGetValue(stat, out var provider)) return "";
        if (!provider.Selectable)
        {
            return $" '{stat}' is provided by the registered ruleset '{provider.Name}', but that name cannot be " +
                $"selected in {StorySource.ManifestFileName}. Register it with a valid ruleset id in C# or remove " +
                $"'{stat}' from '{ManifestVocabulary.Ruleset}.{ManifestVocabulary.Disable}'.";
        }

        var value = YamlScalar(provider.Name);
        return $" '{stat}' is provided by the '{provider.Name}' ruleset. In {StorySource.ManifestFileName}, " +
            $"set '{ManifestVocabulary.Base}: {value}' inside the existing '{ManifestVocabulary.Ruleset}:' mapping " +
            $"under '{ManifestVocabulary.Game}:' to disable it there; '{ManifestVocabulary.Ruleset}: {value}' " +
            $"is the shorthand without a disable list. Otherwise remove '{stat}' from " +
            $"'{ManifestVocabulary.Ruleset}.{ManifestVocabulary.Disable}'.";
    }

    private static string DeclareCustom(StatId stat) =>
        $"To declare your own stat, add '- {{ {ManifestVocabulary.Id}: {YamlScalar(stat.Value)} }}' under " +
        $"'{ManifestVocabulary.Game}.{ManifestVocabulary.Ruleset}.{ManifestVocabulary.Stats}' " +
        "(use a ruleset mapping).";

    private Dictionary<StatId, Provider> IndexProviders()
    {
        var providers = new Dictionary<StatId, Provider>();
        foreach (var (name, factory) in _options.Rulesets
            .OrderBy(pair => !IdentifierRules.IsValid(pair.Key))
            .ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var ruleset = _baseRuleset is not null &&
                (name == _baseName || ReferenceEquals(factory, _baseFactory))
                    ? _baseRuleset : factory();
            var provider = new Provider(name, IdentifierRules.IsValid(name));
            foreach (var stat in ruleset.Stats)
                providers.TryAdd(stat.Id, provider);
        }
        return providers;
    }

    private readonly record struct Provider(string Name, bool Selectable);

    private static string YamlScalar(string value) =>
        bool.TryParse(value, out _) || value.Equals("null", StringComparison.OrdinalIgnoreCase)
            ? $"\"{value}\"" : value;
}
