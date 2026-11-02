using Microsoft.Extensions.FileProviders;
using Talewright.Debugger;

namespace Talewright.Cli.Tests;

public sealed class PlayerAssetFileProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"talewright-player-assets-{Guid.NewGuid():N}");

    [Fact]
    public void Player_paths_use_the_existing_player_assets_and_other_paths_use_the_shell()
    {
        var shellRoot = Directory.CreateDirectory(Path.Combine(_root, "shell")).FullName;
        var playerRoot = Directory.CreateDirectory(Path.Combine(_root, "player")).FullName;
        Directory.CreateDirectory(Path.Combine(playerRoot, "Components"));
        Directory.CreateDirectory(Path.Combine(shellRoot, "_content", "Talewright.PlayerExtra"));
        File.WriteAllText(Path.Combine(shellRoot, "player.html"), "shell");
        File.WriteAllText(Path.Combine(shellRoot, "_content", "Talewright.PlayerExtra", "test.js"), "other");
        File.WriteAllText(Path.Combine(playerRoot, "talewright.css"), "player");
        File.WriteAllText(Path.Combine(playerRoot, "Components", "StoryPlayer.razor.js"), "module");
        using var shell = new PhysicalFileProvider(shellRoot);
        using var player = new PhysicalFileProvider(playerRoot);
        var provider = new PlayerAssetFileProvider(shell, player);

        Assert.Equal("shell", Read(provider.GetFileInfo("/player.html")));
        Assert.Equal("player", Read(provider.GetFileInfo("/_content/Talewright.Player/talewright.css")));
        Assert.Equal("module", Read(provider.GetFileInfo("_content/Talewright.Player/Components/StoryPlayer.razor.js")));
        Assert.Equal("other", Read(provider.GetFileInfo("/_content/Talewright.PlayerExtra/test.js")));
        Assert.Contains(provider.GetDirectoryContents("/_content/Talewright.Player/Components"),
            file => file.Name == "StoryPlayer.razor.js");
        Assert.False(provider.GetFileInfo("/_content/Talewright.Player/../shell/index.html").Exists);
    }

    private static string Read(IFileInfo file)
    {
        Assert.True(file.Exists);
        using var reader = new StreamReader(file.CreateReadStream());
        return reader.ReadToEnd();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
