using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Talewright.Cli;

internal sealed class PublishTemplateCache
{
    private const string Repository = "squid-farm/talewright";
    private const string MetadataName = ".talewright-template.json";
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly JsonSerializerOptions MetadataJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly string _cacheRoot;
    private readonly Uri _releaseBaseUri;
    private readonly HttpClient _client;
    private readonly string? _localAssetDirectory;

    internal static PublishTemplateCache Default { get; } = new(
        DefaultCacheRoot(),
        new Uri($"https://github.com/{Repository}/releases/download/"),
        SharedClient,
        Environment.GetEnvironmentVariable("TALEWRIGHT_PUBLISH_TEMPLATE_DIRECTORY"));

    internal PublishTemplateCache(string cacheRoot, Uri releaseBaseUri, HttpClient client, string? localAssetDirectory = null)
    {
        _cacheRoot = cacheRoot;
        _releaseBaseUri = releaseBaseUri;
        _client = client;
        _localAssetDirectory = localAssetDirectory;
    }

    internal string GetTemplate(string version, string target, string? runtime)
    {
        ValidateRequest(version, target, runtime);
        var assetName = $"talewright-publish-template-{target}{(runtime is null ? "" : "-" + runtime)}-{version}.zip";
        var directoryName = Path.GetFileNameWithoutExtension(assetName);
        var versionDirectory = Path.Combine(_cacheRoot, version);
        var cacheDirectory = Path.Combine(versionDirectory, directoryName);
        if (Directory.Exists(cacheDirectory))
        {
            return ValidateCache(cacheDirectory, version, target, runtime);
        }

        Directory.CreateDirectory(versionDirectory);
        var staging = Path.Combine(versionDirectory, $".{directoryName}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var assetUri = new Uri(_releaseBaseUri, $"v{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(assetName)}");
            var checksumUri = new Uri(assetUri + ".sha256");
            var archivePath = Path.Combine(staging, assetName);
            var checksum = _localAssetDirectory is { } localDirectory
                ? CopyLocalAsset(localDirectory, assetName, archivePath)
                : DownloadAsset(assetUri, checksumUri, archivePath);
            VerifyChecksum(archivePath, checksum, assetName);
            var metadata = ExtractTemplate(archivePath, Path.Combine(staging, "payload"));
            if (metadata != new TemplateMetadata(version, target, runtime))
            {
                throw new InvalidDataException(
                    $"Runner template '{assetName}' identifies itself as {metadata.Version}/{metadata.Target}/{metadata.Runtime ?? "no runtime"}.");
            }

            File.WriteAllText(Path.Combine(staging, MetadataName), JsonSerializer.Serialize(metadata));
            File.Delete(archivePath);
            try
            {
                Directory.Move(staging, cacheDirectory);
            }
            catch (IOException) when (Directory.Exists(cacheDirectory))
            {
                return ValidateCache(cacheDirectory, version, target, runtime);
            }

            return ValidateCache(cacheDirectory, version, target, runtime);
        }
        catch (HttpRequestException ex)
        {
            throw new PublishCommand.PublishBuildException(
                $"Could not download runner template '{assetName}' from the Talewright release. Check your internet connection and confirm that this CLI version has published templates.",
                ex);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    internal static string CurrentCliVersion()
    {
        var informationalVersion = typeof(PublishCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informationalVersion?.Split('+', 2)[0];
        if (version is null || version.Length == 0)
        {
            throw new InvalidDataException("This CLI has no release version for locating its runner templates.");
        }

        ValidateVersion(version);
        return version;
    }

    private static string DefaultCacheRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new IOException("The local application-data directory is unavailable; cannot cache runner templates.");
        }

        return Path.Combine(localData, "Talewright", "publish-templates");
    }

    private static void ValidateRequest(string version, string target, string? runtime)
    {
        ValidateVersion(version);
        if (target == "web" && runtime is null)
        {
            return;
        }

        if (target == "desktop" && runtime is not null && PublishCommand.DesktopRuntimes.Contains(runtime, StringComparer.Ordinal))
        {
            return;
        }

        throw new ArgumentException("A runner template requires target 'web' or 'desktop' with a supported desktop runtime.");
    }

    private static void ValidateVersion(string version)
    {
        if (version.Length == 0 || !char.IsAsciiLetterOrDigit(version[0]) ||
            version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '+' and not '-'))
        {
            throw new InvalidDataException($"Invalid CLI release version '{version}'.");
        }
    }

    private string ValidateCache(string directory, string version, string target, string? runtime)
    {
        var metadataPath = Path.Combine(directory, MetadataName);
        if (!File.Exists(metadataPath))
        {
            throw new InvalidDataException($"Runner template cache '{directory}' is incomplete. Remove that cache folder and retry.");
        }

        var metadata = ReadMetadata(metadataPath);
        if (metadata != new TemplateMetadata(version, target, runtime))
        {
            throw new InvalidDataException($"Runner template cache '{directory}' does not match this CLI target and version.");
        }

        var payload = Path.Combine(directory, "payload");
        if (target == "web" && !File.Exists(Path.Combine(payload, "index.html")))
        {
            throw new InvalidDataException($"Runner template cache '{directory}' has no web entry point. Remove that cache folder and retry.");
        }

        if (target == "desktop")
        {
            var executable = runtime!.StartsWith("win-", StringComparison.Ordinal)
                ? "Talewright.Host.Desktop.exe"
                : "Talewright.Host.Desktop";
            if (!File.Exists(Path.Combine(payload, executable)))
            {
                throw new InvalidDataException($"Runner template cache '{directory}' has no desktop executable. Remove that cache folder and retry.");
            }
        }

        return payload;
    }

    private string DownloadAsset(Uri assetUri, Uri checksumUri, string archivePath)
    {
        Download(assetUri, archivePath);
        return DownloadText(checksumUri);
    }

    private static string CopyLocalAsset(string directory, string assetName, string destination)
    {
        var source = Path.Combine(directory, assetName);
        var checksum = source + ".sha256";
        if (!File.Exists(source) || !File.Exists(checksum))
        {
            throw new FileNotFoundException($"Runner template archive or checksum is missing from '{directory}'.", source);
        }

        File.Copy(source, destination);
        return File.ReadAllText(checksum);
    }

    private string DownloadText(Uri uri)
    {
        using var response = _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private void Download(Uri uri, string path)
    {
        using var response = _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var source = response.Content.ReadAsStream();
        using var destination = File.Create(path);
        source.CopyTo(destination);
    }

    private static void VerifyChecksum(string archivePath, string checksum, string assetName)
    {
        var expected = checksum.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException($"Runner template checksum for '{assetName}' is malformed.");
        }

        using var stream = File.OpenRead(archivePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected),
                Convert.FromHexString(actual)))
        {
            throw new InvalidDataException($"Runner template '{assetName}' failed its SHA-256 checksum.");
        }
    }

    private static TemplateMetadata ExtractTemplate(string archivePath, string payload)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var metadataEntries = archive.Entries.Where(entry => entry.FullName == MetadataName).ToArray();
        if (metadataEntries.Length != 1)
        {
            throw new InvalidDataException($"Runner template archive must contain exactly one '{MetadataName}' entry.");
        }

        using var metadataStream = metadataEntries[0].Open();
        var metadata = ReadMetadata(metadataStream);
        Directory.CreateDirectory(payload);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName == MetadataName)
            {
                continue;
            }

            var name = entry.FullName.Replace('\\', '/');
            var parts = name.Split('/');
            if (name.Length == 0 || name.StartsWith('/') || Path.IsPathRooted(name) ||
                parts.Any(part => part is "" or "." or "..") || !seen.Add(name))
            {
                throw new InvalidDataException($"Runner template archive contains an unsafe or duplicate path '{entry.FullName}'.");
            }

            var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            var fileType = mode & 0xF000;
            if (fileType == 0xA000)
            {
                throw new InvalidDataException($"Runner template archive contains a symbolic link '{entry.FullName}'.");
            }

            if (fileType is not (0 or 0x8000))
            {
                throw new InvalidDataException($"Runner template archive contains a non-file entry '{entry.FullName}'.");
            }

            var destination = Path.GetFullPath(Path.Combine(payload, name.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = Path.GetFullPath(payload) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!destination.StartsWith(prefix, comparison))
            {
                throw new InvalidDataException($"Runner template archive path '{entry.FullName}' escapes its output folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var source = entry.Open())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(output);
            }

            if (!OperatingSystem.IsWindows() && mode != 0)
            {
                File.SetUnixFileMode(destination, (UnixFileMode)(mode & 0x1FF));
            }
        }

        return metadata;
    }

    private static TemplateMetadata ReadMetadata(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadMetadata(stream);
    }

    private static TemplateMetadata ReadMetadata(Stream stream)
    {
        try
        {
            return JsonSerializer.Deserialize<TemplateMetadata>(stream, MetadataJsonOptions)
                ?? throw new InvalidDataException("Runner template metadata is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Runner template metadata is malformed.", ex);
        }
    }

    private sealed record TemplateMetadata(string Version, string Target, string? Runtime);
}
