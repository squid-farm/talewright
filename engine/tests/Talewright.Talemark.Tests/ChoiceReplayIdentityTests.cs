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
        Assert.Equal(ChoiceReplayIdentity.CompactLength, ChoiceReplayIdentity.ForDivert(hub, first).Length);

        ChoiceReplayIdentity.Assign(story.Scenes, description =>
        {
            var value = SHA256.HashData(description);
            "collisio"u8.CopyTo(value);
            return value;
        });

        Assert.Equal(ChoiceReplayIdentity.FullLength, ChoiceReplayIdentity.ForDivert(hub, first).Length);
        Assert.Equal(ChoiceReplayIdentity.FullLength, ChoiceReplayIdentity.ForDivert(hub, second).Length);
        Assert.NotEqual(ChoiceReplayIdentity.ForDivert(hub, first), ChoiceReplayIdentity.ForDivert(hub, second));
    }
}
