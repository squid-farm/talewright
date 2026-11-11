using Talewright.Debugger;

namespace Talewright.Cli.Tests.DebuggerComponents;

internal static class DebuggerDecisionStory
{
    public const string Tale = """
        === hall
        @title Great Hall
        @hub shop
        @hub-limit 2
        {if false}
        * [Unreached] (requires flag:ready, shown "Not evaluated") -> cave
        {/if}
        * [Available] (requires true, shown "Alternate {var:count}") -> cave
        * [Shown] (requires flag:ready, shown "Need {var:count} keys") -> cave
        * [Hidden] (requires flag:ready) -> cave
        * [Climb] (requires flag:ready, shown "Need a rope") skill-check athletics dc 5
          success: -> cave
          failure: -> cave
        * [Loop] { var:count += 1 } -> hall

        === available-offer
        @title Available offer
        @tags shop
        @priority 10
        @requires true
        -> END

        === shown-offer
        @title Shown offer
        @tags shop
        @priority 9
        @requires flag:ready, shown "Need a key"
        -> END

        === limited-offer
        @title Limited offer
        @tags shop
        @priority 8
        @requires flag:ready, shown "Later"
        -> END

        === hidden-offer
        @title Hidden offer
        @tags shop
        @priority 100
        @requires flag:ready
        -> END

        === cave
        @title Cave
        * [Finish] -> END
        """;

    public const string ReentryTale = """
        === hall
        @title Great Hall
        {if var:count > 0}
        -> cave
        {/if}
        * [Shown] (requires flag:ready, shown "Need a key") -> cave
        * [Loop] { var:count += 1 } -> hall

        === cave
        @title Cave
        * [Finish] -> END
        """;

    public static WorkbenchModel Start(string tale = Tale)
    {
        var files = ComponentStory.Files();
        files["main.tale"] = tale;
        var model = ComponentStory.Load(files);
        model.Start();
        Assert.Null(model.Error);
        return model;
    }
}
