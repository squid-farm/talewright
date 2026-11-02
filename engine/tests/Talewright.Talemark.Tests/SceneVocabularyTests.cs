using Talewright.Talemark.Vocabulary;

namespace Talewright.Talemark.Tests;

public sealed class SceneVocabularyTests
{
    [Fact]
    public void Metadata_spellings_roundtrip_to_one_typed_key()
    {
        var keys = Enum.GetValues<SceneMetadataKey>();
        var spellings = keys.Select(SceneVocabulary.Spelling).ToArray();

        Assert.Equal(keys.Length, spellings.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(SceneVocabulary.MetadataSpellings.Order(StringComparer.Ordinal),
            spellings.Order(StringComparer.Ordinal));
        foreach (var key in keys)
        {
            Assert.True(SceneVocabulary.TryGetMetadataKey(SceneVocabulary.Spelling(key), out var parsed));
            Assert.Equal(key, parsed);
        }

        Assert.False(SceneVocabulary.TryGetMetadataKey("unknown", out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hub_mode_prefix_roundtrips(bool all)
    {
        var mode = all ? SceneHubMode.All : SceneHubMode.Any;
        var prefix = SceneVocabulary.HubModePrefix(mode);

        Assert.True(SceneVocabulary.TryGetHubMode(prefix, out var parsed));
        Assert.Equal(mode, parsed);
    }
}
