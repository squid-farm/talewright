using System.Collections;
using Talewright.Core.Identity;
using Talewright.Core.Rulesets;
using Talewright.Rulesets.Generic;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Talemark.Tests;

public sealed class StatDiagnosticGuidanceTests
{
    private static readonly string[] Sites =
    [
        "substitution", "condition", "effect", "choice-check", "block-check",
        "character", "item", "creation", "preset",
    ];

    public static IEnumerable<object[]> UnknownStatCases()
    {
        var configurations = new (string Ruleset, string Stat, string Provider, bool Disabled)[]
        {
            ("base: none", "hp", "rpg", false),
            ("base: rpg, disable: [hp]", "hp", "rpg", true),
            ("base: none", "spark", "astral", false),
            ("base: astral, disable: [spark]", "spark", "astral", true),
        };
        foreach (var (ruleset, stat, provider, disabled) in configurations)
        foreach (var site in Sites)
            yield return [site, ruleset, stat, provider, disabled];
    }

    [Theory]
    [MemberData(nameof(UnknownStatCases))]
    public void Unknown_stats_explain_their_provider_or_disabled_base(
        string site, string ruleset, string stat, string provider, bool disabled)
    {
        var result = StoryCompiler.Compile(Source(site, ruleset, stat), Options());

        Assert.False(result.Success);
        var code = site is "character" or "item" or "creation" or "preset" ? "TW4005" : "TW2001";
        var diagnostic = Assert.Single(result.Errors, item => item.Code == code);
        var prefix = site switch
        {
            "substitution" or "condition" => $"Unknown property '{stat}'",
            "creation" => $"Unknown creation stat '{stat}'",
            _ => $"Unknown stat '{stat}'",
        };
        Assert.StartsWith(prefix, diagnostic.Message);
        Assert.Contains("story.talewright.yaml", diagnostic.Message);
        Assert.Contains($"'{provider}'", diagnostic.Message);
        Assert.Contains("game.ruleset.stats", diagnostic.Message);
        Assert.Contains($"id: {stat}", diagnostic.Message);
        if (disabled)
        {
            Assert.Contains("ruleset.disable", diagnostic.Message);
            Assert.Contains("removed", diagnostic.Message);
        }
        else
        {
            Assert.Contains($"ruleset: {provider}", diagnostic.Message);
            Assert.Contains($"base: {provider}", diagnostic.Message);
        }

        Assert.DoesNotContain(result.Errors, item => item.Code == "TW3005");
        Assert.Equal(code == "TW4005" ? StorySource.ManifestFileName : "main.tale", diagnostic.Location.File);
    }

    [Theory]
    [InlineData("base: none, disable: [hp]", "hp", "rpg")]
    [InlineData("base: none, disable: [spark]", "spark", "astral")]
    [InlineData("base: rpg, disable: [hp, spark]", "spark", "astral")]
    [InlineData("base: astral, disable: [spark, hp]", "hp", "rpg")]
    public void Disabling_a_stat_missing_from_the_base_names_its_provider(
        string ruleset, string stat, string provider)
    {
        var result = StoryCompiler.Compile(Source("none", ruleset, stat), Options());

        var diagnostic = Assert.Single(result.Errors, item => item.Code == "TW4005");
        Assert.StartsWith($"Cannot disable '{stat}'", diagnostic.Message);
        Assert.Contains($"'{provider}'", diagnostic.Message);
        Assert.Contains($"ruleset: {provider}", diagnostic.Message);
        Assert.Contains($"base: {provider}", diagnostic.Message);
        Assert.Contains("story.talewright.yaml", diagnostic.Message);
        Assert.Contains("ruleset.disable", diagnostic.Message);
    }

    [Fact]
    public void Repeated_disable_entry_keeps_its_existing_message()
    {
        var result = StoryCompiler.Compile(Source("none", "base: rpg, disable: [hp, hp]", "hp"), Options());

        var diagnostic = Assert.Single(result.Errors, item => item.Code == "TW4005");
        Assert.Equal("Cannot disable 'hp': the 'rpg' ruleset has no such stat.", diagnostic.Message);
    }

    public static IEnumerable<object[]> UnknownTypoCases()
    {
        var configurations = new (string Ruleset, string Id)[]
        {
            ("base: none", "none"), ("base: rpg", "rpg"), ("base: astral", "cosmic-core"),
        };
        foreach (var (ruleset, id) in configurations)
        foreach (var site in Sites)
            yield return [site, ruleset, id];
    }

    [Theory]
    [MemberData(nameof(UnknownTypoCases))]
    public void Names_no_registered_ruleset_defines_keep_the_existing_message(
        string site, string ruleset, string id)
    {
        var result = StoryCompiler.Compile(Source(site, ruleset, "hpe"), Options());
        var code = site is "character" or "item" or "creation" or "preset" ? "TW4005" : "TW2001";
        var diagnostic = Assert.Single(result.Errors, item => item.Code == code);

        var expected = site switch
        {
            "substitution" or "condition" => "Unknown property 'hpe' on 'player'.",
            "effect" => $"Unknown stat 'hpe' (not in ruleset '{id}').",
            "choice-check" or "block-check" => $"Unknown stat 'hpe' (ruleset '{id}').",
            "creation" => "Unknown creation stat 'hpe'.",
            _ => $"Unknown stat 'hpe' (ruleset '{id}'). Add it under ruleset.stats.",
        };
        Assert.Equal(expected, diagnostic.Message);
    }

    [Fact]
    public void Disabling_a_name_no_registered_ruleset_defines_keeps_the_existing_message()
    {
        var result = StoryCompiler.Compile(Source("none", "base: rpg, disable: [hpe]", "hpe"), Options());

        var diagnostic = Assert.Single(result.Errors, item => item.Code == "TW4005");
        Assert.Equal("Cannot disable 'hpe': the 'rpg' ruleset has no such stat.", diagnostic.Message);
    }

    [Theory]
    [InlineData("hp", "rpg")]
    [InlineData("spark", "astral")]
    public void Suggested_provider_settings_and_custom_declarations_compile(string stat, string provider)
    {
        var options = Options();
        var byBase = StoryCompiler.Compile(Source("effect", $"base: {provider}", stat), options);
        var byShorthand = StoryCompiler.Compile(SourceWithShorthand("effect", provider, stat), options);
        var byDeclaration = StoryCompiler.Compile(
            Source("effect", $"base: none, stats: [{{id: {stat}}}]", stat), options);

        Assert.True(byBase.Success, string.Join("\n", byBase.Diagnostics));
        Assert.True(byShorthand.Success, string.Join("\n", byShorthand.Diagnostics));
        Assert.True(byDeclaration.Success, string.Join("\n", byDeclaration.Diagnostics));
    }

    [Theory]
    [InlineData("hp", "rpg")]
    [InlineData("spark", "astral")]
    public void Removing_a_disable_or_redeclaring_the_stat_compiles(string stat, string provider)
    {
        var options = Options();
        var enabled = StoryCompiler.Compile(Source("effect", $"base: {provider}, disable: []", stat), options);
        var redeclared = StoryCompiler.Compile(
            Source("effect", $"base: {provider}, disable: [{stat}], stats: [{{id: {stat}}}]", stat), options);

        Assert.True(enabled.Success, string.Join("\n", enabled.Diagnostics));
        Assert.True(redeclared.Success, string.Join("\n", redeclared.Diagnostics));
    }

    [Theory]
    [InlineData("hp", "rpg")]
    [InlineData("spark", "astral")]
    public void Invalid_disable_guidance_can_be_followed_without_losing_the_disable_list(
        string stat, string provider)
    {
        var options = Options();
        var changedBase = StoryCompiler.Compile(
            Source("none", $"base: {provider}, disable: [{stat}]", stat), options);
        var removedEntry = StoryCompiler.Compile(Source("none", "base: none, disable: []", stat), options);

        Assert.True(changedBase.Success, string.Join("\n", changedBase.Diagnostics));
        Assert.True(removedEntry.Success, string.Join("\n", removedEntry.Diagnostics));
    }

    [Fact]
    public void Documented_ruleset_and_custom_stat_examples_compile()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Talewright.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var reference = File.ReadAllText(Path.Combine(root.FullName, "docs", "reference", "diagnostics.md"));
        var section = reference.Split("For an unknown stat,", StringSplitOptions.None)[1]
            .Split("\n## TW3xxx", StringSplitOptions.None)[0];
        var blocks = System.Text.RegularExpressions.Regex.Matches(section, @"```yaml\r?\n([\s\S]*?)\r?\n```");
        Assert.Equal(2, blocks.Count);

        var stories = new[]
        {
            "=== start\n~ player:hp += 1\n-> END\n",
            "=== start\n* [Test] skill-check nerve dc 1\n  success: -> END\n  failure: -> END\n",
        };
        for (var index = 0; index < blocks.Count; index++)
        {
            var yaml = blocks[index].Groups[1].Value.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.StartsWith("game:\n", yaml);
            yaml = yaml.Replace("game:\n",
                "game:\n  id: guidance\n  title: Guidance\n  start: start\n", StringComparison.Ordinal) +
                "\ncharacters:\n  player: {first-name: Ada}\n";
            var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
            {
                [StorySource.ManifestFileName] = yaml,
                ["main.tale"] = stories[index],
            }));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }
    }

    [Theory]
    [InlineData("base: none", "hp", "rpg", false)]
    [InlineData("base: rpg, disable: [hp]", "hp", "rpg", true)]
    [InlineData("base: none", "spark", "astral", false)]
    [InlineData("base: astral, disable: [spark]", "spark", "astral", true)]
    public void Trigger_conditions_and_effects_share_the_guidance(
        string ruleset, string stat, string provider, bool disabled)
    {
        var original = Source("none", ruleset, stat);
        var files = original.Files.ToDictionary();
        files[StorySource.ManifestFileName] +=
            $"triggers:\n  - {{id: check, when: 'player:{stat} > 0', do: 'player:{stat} += 1'}}\n";
        var result = StoryCompiler.Compile(new StorySource(files), Options());

        var diagnostics = result.Errors.Where(item => item.Code == "TW2001").ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, item => item.Message.StartsWith($"Unknown property '{stat}'"));
        Assert.Contains(diagnostics, item => item.Message.StartsWith($"Unknown stat '{stat}'"));
        Assert.All(diagnostics, item =>
        {
            Assert.Equal(StorySource.ManifestFileName, item.Location.File);
            Assert.Contains($"'{provider}'", item.Message);
            Assert.Contains(disabled ? "ruleset.disable" : $"ruleset: {provider}", item.Message);
        });
    }

    [Fact]
    public void Provider_index_is_built_only_on_an_error_and_once_per_compilation()
    {
        var calls = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["none"] = 0, ["rpg"] = 0, ["astral"] = 0,
        };
        var factories = Factories().ToDictionary(pair => pair.Key, pair =>
        {
            var name = pair.Key;
            var factory = pair.Value;
            return (Func<Ruleset>)(() =>
            {
                calls[name]++;
                return factory();
            });
        }, StringComparer.Ordinal);
        var rulesets = new CountingRulesets(factories);
        var options = new CompilerOptions { Rulesets = rulesets };

        var valid = StoryCompiler.Compile(Source("none", "base: none", "hp"), options);
        Assert.True(valid.Success, string.Join("\n", valid.Diagnostics));
        Assert.Equal(0, rulesets.Enumerations);
        Assert.Equal(1, calls["none"]);
        Assert.Equal(0, calls["rpg"]);
        Assert.Equal(0, calls["astral"]);

        var invalid = MultipleErrorsSource();
        for (var compilation = 1; compilation <= 2; compilation++)
        {
            var result = StoryCompiler.Compile(invalid, options);
            Assert.True(result.Errors.Count() >= 6, string.Join("\n", result.Diagnostics));
            Assert.Equal(compilation, rulesets.Enumerations);
            Assert.Equal(compilation + 1, calls["none"]);
            Assert.Equal(compilation, calls["rpg"]);
            Assert.Equal(compilation, calls["astral"]);
        }
    }

    [Fact]
    public void A_disabled_base_stat_does_not_scan_other_rulesets()
    {
        var rulesets = new CountingRulesets(Factories());
        var options = new CompilerOptions { Rulesets = rulesets };

        var result = StoryCompiler.Compile(Source("effect", "base: rpg, disable: [hp]", "hp"), options);

        Assert.Contains(result.Errors, item => item.Code == "TW2001" && item.Message.Contains("ruleset.disable"));
        Assert.Equal(0, rulesets.Enumerations);
    }

    [Fact]
    public void Provider_index_reuses_the_selected_custom_ruleset_instance()
    {
        var invocations = 0;
        var factories = Factories();
        factories["astral"] = () =>
        {
            invocations++;
            return Astral();
        };
        var rulesets = new CountingRulesets(factories);
        var result = StoryCompiler.Compile(
            Source("none", "base: astral, disable: [hp]", "hp"),
            new CompilerOptions { Rulesets = rulesets });

        var diagnostic = Assert.Single(result.Errors, item => item.Code == "TW4005");
        Assert.Contains("base: rpg", diagnostic.Message);
        Assert.Equal(1, rulesets.Enumerations);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public void Registered_name_not_definition_id_is_used_in_the_setting()
    {
        var result = StoryCompiler.Compile(Source("effect", "base: none", "spark"), Options());

        var diagnostic = Assert.Single(result.Errors, item => item.Code == "TW2001");
        Assert.Contains("ruleset: astral", diagnostic.Message);
        Assert.DoesNotContain("ruleset: cosmic-core", diagnostic.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("Null")]
    [InlineData("true")]
    [InlineData("FALSE")]
    public void Yaml_keyword_registration_names_are_quoted_in_suggested_settings(string provider)
    {
        var factories = Factories();
        factories[provider] = Astral;
        factories.Remove("astral");
        var options = new CompilerOptions { Rulesets = factories };
        var result = StoryCompiler.Compile(Source("effect", "base: none", "spark"), options);

        var diagnostic = Assert.Single(result.Errors, item => item.Code == "TW2001");
        Assert.Contains($"ruleset: \"{provider}\"", diagnostic.Message);
        Assert.Contains($"base: \"{provider}\"", diagnostic.Message);
        var repaired = StoryCompiler.Compile(SourceWithShorthand("effect", $"\"{provider}\"", "spark"), options);
        Assert.True(repaired.Success, string.Join("\n", repaired.Diagnostics));
    }

    [Fact]
    public void Yaml_keyword_stat_ids_are_quoted_in_custom_declaration_guidance()
    {
        var factories = Factories();
        factories["astral"] = () => new RulesetBuilder("cosmic-core")
            .AddStat(new StatDefinition(new StatId("null"), "Null", StatCategory.Custom, 0, 100, 0))
            .Build();
        var options = new CompilerOptions { Rulesets = factories };
        var result = StoryCompiler.Compile(Source("effect", "base: none", "null"), options);

        var diagnostic = Assert.Single(result.Errors, item => item.Code == "TW2001");
        Assert.Contains("id: \"null\"", diagnostic.Message);
        var repaired = StoryCompiler.Compile(
            Source("effect", "base: none, stats: [{id: \"null\"}]", "null"), options);
        Assert.True(repaired.Success, string.Join("\n", repaired.Diagnostics));
    }

    [Fact]
    public void Multiple_providers_are_selected_deterministically_by_registration_name()
    {
        var factories = Factories();
        factories["zodiac"] = Astral;
        var result = StoryCompiler.Compile(
            Source("effect", "base: none", "spark"), new CompilerOptions { Rulesets = factories });

        var diagnostic = Assert.Single(result.Errors, item => item.Code == "TW2001");
        Assert.Contains("ruleset: astral", diagnostic.Message);
    }

    [Fact]
    public void Unselectable_ruleset_registration_is_reported_without_an_invalid_setting()
    {
        var factories = Factories();
        factories["invalid name"] = Astral;
        factories.Remove("astral");
        var options = new CompilerOptions { Rulesets = factories };
        var unknown = StoryCompiler.Compile(Source("effect", "base: none", "spark"), options);
        var disable = StoryCompiler.Compile(
            Source("none", "base: none, disable: [spark]", "spark"), options);

        var unknownDiagnostic = Assert.Single(unknown.Errors, item => item.Code == "TW2001");
        Assert.Contains("'invalid name'", unknownDiagnostic.Message);
        Assert.Contains("valid ruleset id in C#", unknownDiagnostic.Message);
        Assert.Contains("game.ruleset.stats", unknownDiagnostic.Message);
        Assert.DoesNotContain("ruleset: invalid name", unknownDiagnostic.Message);
        var disableDiagnostic = Assert.Single(disable.Errors, item => item.Code == "TW4005");
        Assert.Contains("'invalid name'", disableDiagnostic.Message);
        Assert.Contains("valid ruleset id in C#", disableDiagnostic.Message);
        Assert.Contains("ruleset.disable", disableDiagnostic.Message);
        Assert.DoesNotContain("ruleset: invalid name", disableDiagnostic.Message);
    }

    [Fact]
    public void Selectable_provider_is_preferred_over_an_invalid_registration_name()
    {
        var factories = Factories();
        factories["!invalid"] = Astral;
        var result = StoryCompiler.Compile(
            Source("effect", "base: none", "spark"), new CompilerOptions { Rulesets = factories });

        var diagnostic = Assert.Single(result.Errors, item => item.Code == "TW2001");
        Assert.Contains("ruleset: astral", diagnostic.Message);
        Assert.DoesNotContain("!invalid", diagnostic.Message);
    }

    private static CompilerOptions Options() => new() { Rulesets = Factories() };

    private static Dictionary<string, Func<Ruleset>> Factories() =>
        new(CompilerOptions.DefaultRulesets(), StringComparer.Ordinal) { ["astral"] = Astral };

    private static Ruleset Astral() => new RulesetBuilder("cosmic-core")
        .AddStat(new StatDefinition(new StatId("spark"), "Spark", StatCategory.Custom, 0, 100, 0))
        .WithSkillCheckRule(new D20SkillCheckRule())
        .Build();

    private static StorySource Source(string site, string ruleset, string stat)
    {
        var creation = site switch
        {
            "creation" => $"\n  creation: {{stats: {{{stat}: [1]}}}}",
            "preset" => $"\n  creation: {{presets: {{novice: {{label: Novice, stats: {{{stat}: 1}}}}}}}}",
            _ => "",
        };
        var characterStats = site == "character" ? $", stats: {{{stat}: 1}}" : "";
        var items = site == "item" ? $"\nitems: {{ring: {{name: Ring, modifiers: {{{stat}: 1}}}}}}" : "";
        var yaml = $"game:\n  id: guidance\n  title: Guidance\n  start: start\n  ruleset: {{{ruleset}}}" +
            $"{creation}\ncharacters:\n  player: {{first-name: Ada{characterStats}}}{items}\n";
        var tale = site switch
        {
            "substitution" => $"=== start\nValue: {{player:{stat}}}\n-> END\n",
            "condition" => $"=== start\n{{if player:{stat} > 0}}Yes.{{/if}}\n-> END\n",
            "effect" => $"=== start\n~ player:{stat} += 1\n-> END\n",
            "choice-check" => $"=== start\n* [Test] skill-check {stat} dc 1\n  success: -> END\n  failure: -> END\n",
            "block-check" => $"=== start\n::: skill-check {stat} dc 1\nsuccess: -> END\nfailure: -> END\n:::\n-> END\n",
            _ => "=== start\n-> END\n",
        };
        return new StorySource(new Dictionary<string, string>
        {
            [StorySource.ManifestFileName] = yaml,
            ["main.tale"] = tale,
        });
    }

    private static StorySource SourceWithShorthand(string site, string ruleset, string stat)
    {
        var source = Source(site, $"base: {ruleset}", stat);
        return new StorySource(source.Files.ToDictionary(pair => pair.Key, pair =>
            pair.Key == StorySource.ManifestFileName
                ? pair.Value.Replace($"ruleset: {{base: {ruleset}}}", $"ruleset: {ruleset}", StringComparison.Ordinal)
                : pair.Value));
    }

    private static StorySource MultipleErrorsSource() => new(new Dictionary<string, string>
    {
        [StorySource.ManifestFileName] = """
            game:
              id: guidance
              title: Guidance
              start: start
              ruleset: {base: none, disable: [spark]}
              creation:
                stats: {hp: [1]}
                presets: {novice: {stats: {spark: 1}}}
            characters:
              player: {first-name: Ada, stats: {spark: 1}}
            items:
              ring: {name: Ring, modifiers: {hp: 1}}
            triggers:
              - {id: check, when: 'player:hp > 0', do: 'player:spark += 1'}
            """,
        ["main.tale"] = """
            === start
            {player:hp}
            ~ player:spark += 1
            * [Test] skill-check hp dc 1
              success: -> END
              failure: -> END
            """,
        ["other.tale"] = "=== other\n~ player:spark += 1\n-> END\n",
    });

    private sealed class CountingRulesets(Dictionary<string, Func<Ruleset>> factories)
        : IReadOnlyDictionary<string, Func<Ruleset>>
    {
        public int Enumerations { get; private set; }
        public Func<Ruleset> this[string key] => factories[key];
        public IEnumerable<string> Keys => factories.Keys;
        public IEnumerable<Func<Ruleset>> Values => factories.Values;
        public int Count => factories.Count;
        public bool ContainsKey(string key) => factories.ContainsKey(key);
        public bool TryGetValue(string key, out Func<Ruleset> value) => factories.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, Func<Ruleset>>> GetEnumerator()
        {
            Enumerations++;
            return factories.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
