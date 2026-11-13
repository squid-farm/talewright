using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Talewright.Debugger;
using Talewright.Talemark.Compilation;

namespace Talewright.Cli.Tests.DebuggerComponents;

public sealed class DebuggerToolbarTests : BunitContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compiler_errors_open_problems_without_restarting(bool requireRestart)
    {
        using var model = ComponentStory.Load();
        var files = ComponentStory.Files();
        files["main.tale"] = ComponentStory.Tale.Replace("-> cave", "-> nowhere", StringComparison.Ordinal);
        var source = new StorySource(files);
        var compilation = StoryCompiler.Compile(source);

        Assert.False(compilation.Success);
        Assert.False(model.ApplySource(new(source, compilation, null), yamlChanged: requireRestart));
        Assert.Equal(requireRestart, model.RestartRequired);

        var restartCalls = 0;
        var problemsCalls = 0;
        var toolbar = Render<DebuggerToolbar>(parameters => parameters
            .Add(component => component.Model, model)
            .Add(component => component.OnRestart, EventCallback.Factory.Create(this, () => { restartCalls++; }))
            .Add(component => component.OnShowProblems, EventCallback.Factory.Create(this, () => { problemsCalls++; })));

        var status = toolbar.Find(".source-status");
        Assert.Equal("Open Problems", status.GetAttribute("aria-label"));
        await status.ClickAsync(new MouseEventArgs());

        Assert.Equal(0, restartCalls);
        Assert.Equal(1, problemsCalls);
    }
}
