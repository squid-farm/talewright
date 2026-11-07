using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Talewright.Player.Components;
using Talewright.Player.Diagnostics;
using Talewright.Player.Library;
using Talewright.Player.Preferences;
using Talewright.Talemark.Compilation;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Player.E2E.Tests;

public sealed class PlayerDiagnosticsTests
{
    [Fact]
    public void Report_includes_the_loaded_player_build_identity()
    {
        var identity = PlayerBuildIdentity.Current;
        var report = new PlayerDiagnostics().CreateReport();

        Assert.Contains($"Player version: {identity.Version}{Environment.NewLine}", report);
        Assert.Contains($"Player commit: {identity.Commit}{Environment.NewLine}", report);
    }

    [Fact]
    public void Buffer_retains_only_the_most_recent_entries_and_is_session_local()
    {
        var diagnostics = new PlayerDiagnostics();
        for (var i = 0; i < PlayerDiagnostics.Capacity + 5; i++)
        {
            diagnostics.SetContext($"story-{i}", $"scene-{i}");
            diagnostics.Record(PlayerDiagnosticOperation.SaveWrite, PlayerDiagnosticCause.StorageOrNetwork);
        }

        var report = diagnostics.CreateReport();
        Assert.Contains("Recent warnings and errors: 100 (maximum 100)", report);
        Assert.DoesNotContain("story=story-4  scene=scene-4", report);
        Assert.Contains("story=story-5  scene=scene-5", report);
        Assert.Contains("story=story-104  scene=scene-104", report);
        Assert.Contains("Story ID: story-104", report);
        Assert.Contains("Scene ID: scene-104", report);
        Assert.Contains("Recent warnings and errors: 0", new PlayerDiagnostics().CreateReport());
    }

    [Fact]
    public void Concurrent_recording_still_obeys_the_capacity()
    {
        var diagnostics = new PlayerDiagnostics();
        Parallel.For(0, 1000, _ =>
            diagnostics.Record(PlayerDiagnosticOperation.SaveLoad, PlayerDiagnosticCause.InvalidData));

        var report = diagnostics.CreateReport();
        Assert.Contains("Recent warnings and errors: 100 (maximum 100)", report);
        Assert.Equal(100, report.Split('\n').Count(line => line.Contains("Load saved game: invalid data", StringComparison.Ordinal)));
    }

    [Fact]
    public void Context_changes_notify_only_when_the_sanitized_context_changes()
    {
        var diagnostics = new PlayerDiagnostics();
        var changes = 0;
        diagnostics.Changed += () =>
        {
            changes++;
            _ = diagnostics.CreateReport();
        };

        diagnostics.SetContext("story-1", "start");
        diagnostics.SetContext("story-1", "start");
        Assert.Equal(1, changes);
        diagnostics.SetContext("story-1", "second");
        diagnostics.Record(PlayerDiagnosticOperation.SaveLoad, PlayerDiagnosticCause.InvalidData);
        Assert.Equal(3, changes);
        Assert.Contains("Scene ID: second", diagnostics.CreateReport());
    }

    [Fact]
    public void Exception_details_compiler_messages_paths_and_unsafe_ids_never_enter_the_report()
    {
        const string secret = "PRIVATE_CONTENT_5f2d";
        var diagnostics = new PlayerDiagnostics();
        diagnostics.SetContext("valid-story", "start");
        diagnostics.Record(PlayerDiagnosticOperation.SaveImport,
            new InvalidDataException($"Save bytes {secret} from /home/author/private/save.twsave",
                new IOException($"https://private.example/?token={secret}")));
        diagnostics.RecordCompiler(new Diagnostic(DiagnosticSeverity.Warning, "TW2002", secret,
            new SourceLocation("/home/author/private/story.tale", 42, 3)));
        diagnostics.RecordCompiler(new Diagnostic(DiagnosticSeverity.Error, $"TW2002-{secret}", secret,
            new SourceLocation("secret.tale", 1, 1)));
        diagnostics.SetContext($"/home/author/{secret}", $"https://private.example/{secret}");
        diagnostics.Record(PlayerDiagnosticOperation.StoryOpen, new IOException(secret), includeContext: false);

        var report = diagnostics.CreateReport();
        Assert.Contains("Import saved game: invalid data  story=valid-story  scene=start", report);
        Assert.Contains("Warning  Compile story: compiler diagnostic (TW2002)", report);
        Assert.Contains("Story ID: unavailable", report);
        Assert.Contains("Scene ID: unavailable", report);
        Assert.DoesNotContain(secret, report);
        Assert.DoesNotContain("/home/", report);
        Assert.DoesNotContain("https://", report);
        Assert.DoesNotContain("story.tale", report);
        Assert.DoesNotContain("TW2002-", report);
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("C:\\private\\story")]
    [InlineData("https://example.test/secret")]
    [InlineData("story\nsecret")]
    [InlineData("")]
    public void Invalid_identifiers_are_omitted(string identifier)
    {
        var diagnostics = new PlayerDiagnostics();
        diagnostics.SetContext(identifier, identifier);
        diagnostics.Record(PlayerDiagnosticOperation.ImageLoad, PlayerDiagnosticCause.NotFound);
        var report = diagnostics.CreateReport();
        Assert.Contains("Story ID: unavailable", report);
        Assert.Contains("Scene ID: unavailable", report);
        Assert.DoesNotContain("story=", report);
        Assert.DoesNotContain("scene=", report);
    }

    [Fact]
    public void Excessively_long_identifiers_are_omitted()
    {
        var diagnostics = new PlayerDiagnostics();
        diagnostics.SetContext(new string('x', 129), new string('y', 129));
        var report = diagnostics.CreateReport();
        Assert.Contains("Story ID: unavailable", report);
        Assert.Contains("Scene ID: unavailable", report);
    }

    [Fact]
    public void Unscoped_events_do_not_claim_the_previously_open_story()
    {
        var diagnostics = new PlayerDiagnostics();
        diagnostics.SetContext("previous-story", "previous-scene");
        diagnostics.Record(PlayerDiagnosticOperation.StoryUpload, PlayerDiagnosticCause.InvalidData, includeContext: false);
        var entry = Assert.Single(diagnostics.CreateReport().Split('\n'),
            line => line.Contains("Upload story:", StringComparison.Ordinal));
        Assert.DoesNotContain("previous-story", entry);
        Assert.DoesNotContain("previous-scene", entry);
    }

    [Theory]
    [InlineData("missing", "not found")]
    [InlineData("denied", "access denied")]
    [InlineData("js", "host operation failed")]
    [InlineData("io", "storage or network failure")]
    [InlineData("unsupported", "unsupported operation or missing host dependency")]
    public void Exceptions_are_recorded_as_safe_categories(string kind, string expected)
    {
        var exception = kind switch
        {
            "missing" => new FileNotFoundException("private"),
            "denied" => (Exception)new UnauthorizedAccessException("private"),
            "js" => new JSException("private"),
            "io" => new IOException("private"),
            "unsupported" => new NotSupportedException("private"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var diagnostics = new PlayerDiagnostics();
        diagnostics.Record(PlayerDiagnosticOperation.SaveLoad, exception);
        var report = diagnostics.CreateReport();
        Assert.Contains($"Load saved game: {expected}", report);
        Assert.DoesNotContain("private", report);
    }
}

public sealed class PreferenceDiagnosticsTests
{
    [Fact]
    public void The_original_two_argument_constructor_remains_available_to_existing_hosts()
    {
        Assert.NotNull(typeof(PlayerPreferencesStore).GetConstructor(
            [typeof(IJSRuntime), typeof(ILogger<PlayerPreferencesStore>)]));
    }

    [Fact]
    public async Task Existing_hosts_without_diagnostic_services_still_render()
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IJSRuntime>(new PreferencesJs())
            .AddSingleton<IStoryLibrary, EmptyLibrary>()
            .AddSingleton<PlayerPreferencesStore>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<TalewrightApp>(ParameterView.Empty);
            return component.ToHtmlString();
        });

        Assert.Contains("Diagnostic report", html);
        Assert.Contains("Downloading diagnostic reports is unavailable in this host.", html);
    }

    [Fact]
    public async Task Storage_read_and_write_failures_preserve_warnings_and_record_no_exception_details()
    {
        var diagnostics = new PlayerDiagnostics();
        var js = new PreferencesJs { FailRead = true, FailWrite = true };
        await using var preferences = new PlayerPreferencesStore(js, NullLogger<PlayerPreferencesStore>.Instance, diagnostics);

        await preferences.InitializeAsync();
        Assert.Equal("Settings could not be loaded or saved in this host.", preferences.Warning);
        await preferences.SetThemeAsync(PlayerTheme.Dark);
        Assert.Contains("only last for the current session", preferences.Warning);

        var report = diagnostics.CreateReport();
        Assert.Contains("Read player settings: host operation failed", report);
        Assert.Contains("Write player settings: host operation failed", report);
        Assert.DoesNotContain(PreferencesJs.Secret, report);
    }

    [Fact]
    public async Task Malformed_and_unsupported_stored_preferences_are_safe_warnings()
    {
        var malformedReport = new PlayerDiagnostics();
        await using (var preferences = new PlayerPreferencesStore(
            new PreferencesJs { Stored = PreferencesJs.Secret }, NullLogger<PlayerPreferencesStore>.Instance, malformedReport))
        {
            await preferences.InitializeAsync();
            Assert.Equal(PlayerPreferences.Default, preferences.Current);
        }
        Assert.Contains("Warning  Read player settings: invalid data", malformedReport.CreateReport());
        Assert.DoesNotContain(PreferencesJs.Secret, malformedReport.CreateReport());

        var unsupportedReport = new PlayerDiagnostics();
        await using (var preferences = new PlayerPreferencesStore(
            new PreferencesJs { Stored = """{"version":1,"theme":"dark","reducedMotion":"private_value"}""" },
            NullLogger<PlayerPreferencesStore>.Instance, unsupportedReport))
        {
            await preferences.InitializeAsync();
            Assert.Equal(PlayerReducedMotion.System, preferences.Current.ReducedMotion);
        }
        Assert.Contains("Warning  Read player settings: unsupported", unsupportedReport.CreateReport());
        Assert.DoesNotContain("private_value", unsupportedReport.CreateReport());
    }

    private sealed class PreferencesJs : IJSRuntime, IJSObjectReference
    {
        public const string Secret = "PRIVATE_PREFERENCE_CONTENT";
        public bool FailRead { get; init; }
        public bool FailWrite { get; init; }
        public string? Stored { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            object? result = identifier switch
            {
                "import" => this,
                "read" when FailRead => throw new JSException(Secret),
                "read" => Stored,
                "write" when FailWrite => throw new JSException(Secret),
                _ => null,
            };
            return ValueTask.FromResult(result is TValue value ? value : default!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyLibrary : IStoryLibrary
    {
        public Task<IReadOnlyList<StoryEntry>> ListAsync() => Task.FromResult<IReadOnlyList<StoryEntry>>([]);
        public Task<StorySource> LoadAsync(string id) => throw new InvalidOperationException("The library is empty.");
    }
}

public sealed class DiagnosticReportExporterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"talewright-diagnostics-{Guid.NewGuid():N}");

    [Fact]
    public async Task Local_export_honors_cancel_and_writes_the_selected_bytes()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "report.txt");
        await File.WriteAllBytesAsync(destination, [1]);
        string? selected = null;
        string? suggested = null;
        var exporter = new LocalDiagnosticReportExporter(name =>
        {
            suggested = name;
            return Task.FromResult(selected);
        });

        Assert.False(await exporter.ExportAsync("talewright-diagnostics.txt", [2, 3]));
        Assert.Equal("talewright-diagnostics.txt", suggested);
        Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(destination));
        selected = destination;
        Assert.True(await exporter.ExportAsync("talewright-diagnostics.txt", [2, 3]));
        Assert.Equal(new byte[] { 2, 3 }, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Local_export_surfaces_write_failures()
    {
        Directory.CreateDirectory(_directory);
        var exporter = new LocalDiagnosticReportExporter(_ => Task.FromResult<string?>(_directory));
        await Assert.ThrowsAnyAsync<IOException>(() => exporter.ExportAsync("report.txt", [1]));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
