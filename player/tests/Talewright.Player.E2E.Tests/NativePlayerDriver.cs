using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Talewright.Player.E2E.Tests;

public sealed record AccessibleNode(
    string Name, string Role, string Text, bool Focused, PlayerBounds Bounds, AccessibleNode[] Children)
{
    public IEnumerable<AccessibleNode> Descendants() =>
        new[] { this }.Concat(Children.SelectMany(child => child.Descendants()));

    public string Content => Regex.Replace(
        string.Join(" ", Descendants().SelectMany(node => new[] { node.Name, node.Text })), @"\s+", " ").Trim();

    public string NarrativeContent
    {
        get
        {
            // AX text already includes descendant text; object markers and repeated names are not narrative.
            var content = !string.IsNullOrEmpty(Text)
                ? Text.Replace("\uFFFC", "", StringComparison.Ordinal)
                : Children.Length > 0 ? string.Join(" ", Children.Select(node => node.NarrativeContent)) : Name;
            return Regex.Replace(content, @"\s+", " ").Trim();
        }
    }
}

public sealed class NativePlayerDriver(string artifacts, string? executableOverride = null) : IPlayerDriver
{
    private Process? _adapter;
    private Task<string>? _stderr;
    private bool _protocolFailed;
    private readonly string _temporary = Path.Combine(Path.GetTempPath(), "talewright-driver-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task StartAsync(params string[] paths)
    {
        if (_adapter is not null)
        {
            throw new InvalidOperationException("This driver has already started.");
        }

        try
        {
            var executable = executableOverride ?? Environment.GetEnvironmentVariable("TALEWRIGHT_DESKTOP_EXECUTABLE")
                ?? throw new InvalidOperationException("Set TALEWRIGHT_DESKTOP_EXECUTABLE or use player/tests/run_e2e.py linux.");
            var helper = Path.Combine(AppContext.BaseDirectory, "native", "atspi_adapter.py");
            Directory.CreateDirectory(_temporary);
            var start = new ProcessStartInfo("/usr/bin/python3")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { helper, "adapter", "--executable", executable, "--output", artifacts })
            {
                start.ArgumentList.Add(argument);
            }

            _protocolFailed = false;
            _adapter = Process.Start(start) ?? throw new InvalidOperationException("Could not start the AT-SPI adapter.");
            _stderr = _adapter.StandardError.ReadToEndAsync();
            await CommandAsync("launch", new { paths });
        }
        catch (Exception startupError)
        {
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Native startup and cleanup failed.", startupError, cleanupError);
            }

            throw;
        }
    }

    private async Task<JsonElement> CommandAsync(string command, object? arguments = null)
    {
        try
        {
            var adapter = _adapter ?? throw new InvalidOperationException("Start the driver first.");
            if (adapter.HasExited)
            {
                throw new InvalidOperationException($"AT-SPI adapter exited ({adapter.ExitCode}): {await _stderr!}");
            }

            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var request = JsonSerializer.Serialize(new { command, arguments });
            string? line;
            try
            {
                await adapter.StandardInput.WriteLineAsync(request.AsMemory(), deadline.Token);
                await adapter.StandardInput.FlushAsync(deadline.Token);
                line = await adapter.StandardOutput.ReadLineAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new TimeoutException($"AT-SPI command '{command}' exceeded 60 seconds.");
            }

            if (line is null)
            {
                throw new IOException("AT-SPI adapter closed its response stream.");
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
            {
                throw new InvalidOperationException($"AT-SPI command '{command}' failed: {root.GetProperty("error").GetString()}");
            }

            return root.GetProperty("result").Clone();
        }
        catch
        {
            _protocolFailed = true;
            throw;
        }
    }

    public async Task UploadAsync(string name, byte[] content)
    {
        var path = Path.Combine(_temporary, Path.GetFileName(name));
        await File.WriteAllBytesAsync(path, content);
        await CommandAsync("upload", new { path });
    }

    public async Task OpenBundledAsync() => await CommandAsync("click", new { pattern = @".* Built-in$", role = "push button" });
    public async Task ClickAsync(string name) =>
        await CommandAsync("click", new { name, role = "push button" });
    public async Task ClickWithinAsync(string name, string within) =>
        await CommandAsync("click", new { name, role = "push button", within });
    public async Task ChooseAsync(string label) =>
        await CommandAsync("click", new { pattern = @"^\d+\.\s*" + Regex.Escape(label) + "$", role = "push button", within = "Story choices" });
    public async Task PressAsync(string key) => await CommandAsync("key", new { key });
    public async Task ResizeAsync(int width, int height) => await CommandAsync("resize", new { width, height });
    public async Task FillAsync(string name, string value) => await CommandAsync("fill", new { name, value });
    public async Task ActivateAsync(string name) => await CommandAsync("activate", new { name });
    public async Task SaveFileAsync(string name, string path) => await CommandAsync("save-file", new { name, path });
    public async Task ScrollToImageAsync(string name) => await CommandAsync("scroll", new { name, role = "image" });
    public async Task ScrollToButtonAsync(string name) => await CommandAsync("scroll", new { name, role = "push button" });
    public async Task<int> CloseWindowAsync() => (await CommandAsync("close-window")).GetInt32();
    public async Task<AccessibleNode> SnapshotAsync() =>
        (await CommandAsync("snapshot")).Deserialize<AccessibleNode>(Json)
        ?? throw new InvalidDataException("Missing accessibility snapshot.");

    public async Task<PlayerObservation> ObserveAsync()
    {
        var data = await CommandAsync("snapshot");
        var root = data.Deserialize<AccessibleNode>(Json) ?? throw new InvalidDataException("Missing accessibility snapshot.");
        var nodes = root.Descendants().ToArray();
        AccessibleNode? Named(string name) => nodes.FirstOrDefault(node => node.Name == name);
        var passage = Named("Current passage");
        var transcript = Named("Story transcript");
        var sheet = Named("Character sheet");
        var choices = Named("Story choices")?.Descendants()
            .Where(node => node.Role == "push button")
            .Select(node => Regex.Replace(node.Name, @"^\d+\.\s*", "")).ToArray() ?? [];
        var window = nodes.FirstOrDefault(node => node.Role == "frame");
        return new(
            passage?.NarrativeContent ?? "", transcript?.Content ?? "", sheet?.Content ?? "",
            passage is null ? root.Content : "", choices,
            nodes.Any(node => node.Role == "push button" && node.Name == "Play again"),
            passage is not null, passage?.Focused == true,
            passage is not null && window is not null &&
                passage.Bounds.Y >= window.Bounds.Y &&
                passage.Bounds.Y + passage.Bounds.Height <= window.Bounds.Y + window.Bounds.Height,
            sheet is not null && transcript is not null &&
                sheet.Bounds.Y >= transcript.Bounds.Y + transcript.Bounds.Height - 1,
            window?.Bounds.Width ?? 0,
            window?.Name ?? "");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_adapter is not null)
            {
                if (!_adapter.HasExited)
                {
                    if (!_protocolFailed)
                    {
                        await CommandAsync("close");
                    }

                    _adapter.StandardInput.Close();
                    await _adapter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }

                if (_adapter.ExitCode != 0 && !_protocolFailed)
                {
                    throw new InvalidOperationException($"AT-SPI adapter exited with {_adapter.ExitCode}.");
                }
            }
        }
        finally
        {
            if (_adapter is not null)
            {
                if (!_adapter.HasExited)
                {
                    _adapter.Kill(entireProcessTree: true);
                    await _adapter.WaitForExitAsync();
                }

                try
                {
                    await File.WriteAllTextAsync(Path.Combine(artifacts, "adapter.log"), await _stderr!);
                }
                finally
                {
                    _adapter.Dispose();
                    _adapter = null;
                }
            }

            if (Directory.Exists(_temporary))
            {
                Directory.Delete(_temporary, recursive: true);
            }
        }
    }
}
