using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Talewright.Debugger;

internal sealed class PlayerAssetFileProvider(IFileProvider shell, IFileProvider player) : IFileProvider
{
    private const string PlayerPrefix = "_content/Talewright.Player";

    public IFileInfo GetFileInfo(string subpath) =>
        PlayerPath(subpath) is { } path ? player.GetFileInfo(path) : shell.GetFileInfo(subpath);

    public IDirectoryContents GetDirectoryContents(string subpath) =>
        PlayerPath(subpath) is { } path ? player.GetDirectoryContents(path) : shell.GetDirectoryContents(subpath);

    public IChangeToken Watch(string filter) =>
        PlayerPath(filter) is { } path
            ? player.Watch(path)
            : new CompositeChangeToken([shell.Watch(filter), player.Watch(filter)]);

    private static string? PlayerPath(string subpath)
    {
        var path = subpath.TrimStart('/');
        if (path == PlayerPrefix)
        {
            return "";
        }
        return path.StartsWith(PlayerPrefix + "/", StringComparison.Ordinal)
            ? path[(PlayerPrefix.Length + 1)..] : null;
    }
}
