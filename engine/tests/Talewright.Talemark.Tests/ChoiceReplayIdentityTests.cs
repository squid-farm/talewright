using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Talewright.Core.Identity;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Model;

namespace Talewright.Talemark.Tests;

public sealed class ChoiceReplayIdentityTests
{
    [Fact]
    public void Truncated_hash_collision_uses_full_identity_for_every_choice_in_the_scene()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: { id: replay, title: Replay, start: start }
                characters:
                  player: { first-name: Ada }
                """,
            ["main.tale"] = """
                === start
                * [First label] -> finish
                * [Different label, same outcome] -> finish
                * [Different outcome] -> other
                === finish
                Finished.
                -> END
                === other
                Other.
                -> END
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var choices = result.Story!.GetScene(new SceneId("start")).Choices;
        Assert.All(choices, choice => Assert.Equal(ChoiceReplayIdentity.CompactLength, choice.ReplayIdentity!.Length));
        Assert.Equal(choices[0].ReplayIdentity, choices[1].ReplayIdentity);

        ChoiceReplayIdentity.Assign([result.Story.GetScene(new SceneId("start"))], description =>
        {
            var value = SHA256.HashData(description);
            "collisio"u8.CopyTo(value);
            return value;
        });

        Assert.All(choices, choice => Assert.Equal(ChoiceReplayIdentity.FullLength, choice.ReplayIdentity!.Length));
        Assert.Equal(choices[0].ReplayIdentity, choices[1].ReplayIdentity);
        Assert.NotEqual(choices[0].ReplayIdentity, choices[2].ReplayIdentity);
    }

    [Fact]
    public void Hub_collision_detection_includes_destinations_outside_current_tag_eligibility()
    {
        var result = StoryCompiler.Compile(new StorySource(new Dictionary<string, string>
        {
            ["story.talewright.yaml"] = """
                game: { id: hub-replay, title: Hub replay, start: hub }
                characters:
                  player: { first-name: Ada }
                """,
            ["main.tale"] = """
                === hub
                @hub room
                === first
                @tags room
                -> END
                === second
                @tags elsewhere
                -> END
                """,
        }));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var story = result.Story!;
        var hub = story.GetScene(new SceneId("hub"));
        var first = new DivertTarget(new SceneId("first"));
        var second = new DivertTarget(new SceneId("second"));
        var compactIdentity = ChoiceReplayIdentity.ForDivert(hub, first);
        Assert.Equal(ChoiceReplayIdentity.CompactLength, compactIdentity.Length);
        AssertSharedIdentityMemory(compactIdentity, ChoiceReplayIdentity.ForDivert(hub, first));

        ChoiceReplayIdentity.Assign(story.Scenes, description =>
        {
            var value = SHA256.HashData(description);
            "collisio"u8.CopyTo(value);
            return value;
        });

        var fullIdentity = ChoiceReplayIdentity.ForDivert(hub, first);
        Assert.Equal(ChoiceReplayIdentity.FullLength, fullIdentity.Length);
        AssertSharedIdentityMemory(fullIdentity, ChoiceReplayIdentity.ForDivert(hub, first));
        Assert.Equal(ChoiceReplayIdentity.FullLength, ChoiceReplayIdentity.ForDivert(hub, second).Length);
        Assert.NotEqual(fullIdentity, ChoiceReplayIdentity.ForDivert(hub, second));
    }

    private static void AssertSharedIdentityMemory(ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second)
    {
        Assert.True(MemoryMarshal.TryGetArray(first, out var firstSegment));
        Assert.True(MemoryMarshal.TryGetArray(second, out var secondSegment));
        Assert.Same(firstSegment.Array, secondSegment.Array);
        Assert.Equal(firstSegment.Offset, secondSegment.Offset);
        Assert.Equal(firstSegment.Count, secondSegment.Count);
    }
}
