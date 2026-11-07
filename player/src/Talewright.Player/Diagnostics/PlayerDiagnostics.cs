using System.Runtime.InteropServices;
using System.Text;
using Microsoft.JSInterop;
using Talewright.Talemark.Diagnostics;

namespace Talewright.Player.Diagnostics;

public enum PlayerDiagnosticOperation
{
    StoryOpen,
    StoryUpload,
    StoryCompile,
    ImageLoad,
    AutosaveRead,
    AutosaveWrite,
    TriggerSave,
    SaveList,
    SaveWrite,
    SaveLoad,
    SaveImport,
    SaveExport,
    PreferencesRead,
    PreferencesWrite,
    ReportExport,
}

public enum PlayerDiagnosticCause
{
    Unavailable,
    NotFound,
    InvalidData,
    AccessDenied,
    StorageOrNetwork,
    HostFailure,
    InvalidOperation,
    Unsupported,
    CompilerDiagnostic,
}

public sealed class PlayerDiagnostics
{
    public const int Capacity = 100;
    private readonly object _gate = new();
    private readonly Queue<Entry> _entries = new();
    private string? _storyId;
    private string? _sceneId;

    private sealed record Entry(DateTimeOffset Time, bool Warning, PlayerDiagnosticOperation Operation,
        PlayerDiagnosticCause Cause, string? Code, string? StoryId, string? SceneId);

    public event Action? Changed;

    public void SetContext(string? storyId, string? sceneId)
    {
        var safeStoryId = SafeIdentifier(storyId);
        var safeSceneId = SafeIdentifier(sceneId);
        lock (_gate)
        {
            if (_storyId == safeStoryId && _sceneId == safeSceneId) return;
            _storyId = safeStoryId;
            _sceneId = safeSceneId;
        }
        Changed?.Invoke();
    }

    public void Record(PlayerDiagnosticOperation operation, Exception exception, bool warning = false, bool includeContext = true) =>
        Record(operation, CauseFor(exception), warning, includeContext);

    public void Record(PlayerDiagnosticOperation operation, PlayerDiagnosticCause cause, bool warning = false, bool includeContext = true)
    {
        Add(operation, cause, warning, null, includeContext);
    }

    public void RecordCompiler(Diagnostic diagnostic, bool includeContext = true)
    {
        var code = diagnostic.Code;
        var safeCode = code.Length == 6 && code.StartsWith("TW", StringComparison.Ordinal) &&
            code.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0 ? code : null;
        Add(PlayerDiagnosticOperation.StoryCompile, PlayerDiagnosticCause.CompilerDiagnostic,
            diagnostic.Severity == DiagnosticSeverity.Warning, safeCode, includeContext);
    }

    public string CreateReport()
    {
        Entry[] entries;
        string? storyId;
        string? sceneId;
        lock (_gate)
        {
            entries = _entries.ToArray();
            storyId = _storyId;
            sceneId = _sceneId;
        }

        var identity = PlayerBuildIdentity.Current;
        var report = new StringBuilder();
        report.AppendLine("Talewright diagnostic report");
        report.AppendLine($"Player version: {identity.Version}");
        report.AppendLine($"Player commit: {identity.Commit}");
        report.AppendLine($"Host: {(OperatingSystem.IsBrowser() ? "Browser" : "Desktop")}");
        report.AppendLine($"Platform: {Platform()} ({RuntimeInformation.ProcessArchitecture})");
        report.AppendLine($".NET runtime: {Environment.Version}");
        report.AppendLine($"Story ID: {storyId ?? "unavailable"}");
        report.AppendLine($"Scene ID: {sceneId ?? "unavailable"}");
        report.AppendLine($"Recent warnings and errors: {entries.Length} (maximum {Capacity})");
        foreach (var entry in entries)
        {
            report.Append(entry.Time.ToUniversalTime().ToString("O")).Append("  ")
                .Append(entry.Warning ? "Warning" : "Error").Append("  ")
                .Append(Describe(entry.Operation)).Append(": ").Append(Describe(entry.Cause));
            if (entry.Code is not null) report.Append(" (").Append(entry.Code).Append(')');
            if (entry.StoryId is not null) report.Append("  story=").Append(entry.StoryId);
            if (entry.SceneId is not null) report.Append("  scene=").Append(entry.SceneId);
            report.AppendLine();
        }
        return report.ToString();
    }

    private void Add(PlayerDiagnosticOperation operation, PlayerDiagnosticCause cause, bool warning, string? code, bool includeContext)
    {
        lock (_gate)
        {
            if (_entries.Count == Capacity) _entries.Dequeue();
            _entries.Enqueue(new(DateTimeOffset.UtcNow, warning, operation, cause, code,
                includeContext ? _storyId : null, includeContext ? _sceneId : null));
        }
        Changed?.Invoke();
    }

    private static string? SafeIdentifier(string? value) =>
        value is { Length: > 0 and <= 128 } && !value.Contains("..", StringComparison.Ordinal) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') ? value : null;

    private static PlayerDiagnosticCause CauseFor(Exception exception) => exception switch
    {
        FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException => PlayerDiagnosticCause.NotFound,
        InvalidDataException or System.Text.Json.JsonException => PlayerDiagnosticCause.InvalidData,
        UnauthorizedAccessException => PlayerDiagnosticCause.AccessDenied,
        JSException => PlayerDiagnosticCause.HostFailure,
        NotSupportedException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException =>
            PlayerDiagnosticCause.Unsupported,
        IOException => PlayerDiagnosticCause.StorageOrNetwork,
        _ => PlayerDiagnosticCause.InvalidOperation,
    };

    private static string Platform() =>
        OperatingSystem.IsBrowser() ? "Browser" :
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsMacOS() ? "macOS" :
        OperatingSystem.IsLinux() ? "Linux" : "Unknown";

    private static string Describe(PlayerDiagnosticOperation operation) => operation switch
    {
        PlayerDiagnosticOperation.StoryOpen => "Open story",
        PlayerDiagnosticOperation.StoryUpload => "Upload story",
        PlayerDiagnosticOperation.StoryCompile => "Compile story",
        PlayerDiagnosticOperation.ImageLoad => "Load image",
        PlayerDiagnosticOperation.AutosaveRead => "Read autosave",
        PlayerDiagnosticOperation.AutosaveWrite => "Write autosave",
        PlayerDiagnosticOperation.TriggerSave => "Write trigger checkpoint",
        PlayerDiagnosticOperation.SaveList => "List saved games",
        PlayerDiagnosticOperation.SaveWrite => "Write saved game",
        PlayerDiagnosticOperation.SaveLoad => "Load saved game",
        PlayerDiagnosticOperation.SaveImport => "Import saved game",
        PlayerDiagnosticOperation.SaveExport => "Export saved game",
        PlayerDiagnosticOperation.PreferencesRead => "Read player settings",
        PlayerDiagnosticOperation.PreferencesWrite => "Write player settings",
        PlayerDiagnosticOperation.ReportExport => "Export diagnostic report",
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static string Describe(PlayerDiagnosticCause cause) => cause switch
    {
        PlayerDiagnosticCause.Unavailable => "unavailable",
        PlayerDiagnosticCause.NotFound => "not found",
        PlayerDiagnosticCause.InvalidData => "invalid data",
        PlayerDiagnosticCause.AccessDenied => "access denied",
        PlayerDiagnosticCause.StorageOrNetwork => "storage or network failure",
        PlayerDiagnosticCause.HostFailure => "host operation failed",
        PlayerDiagnosticCause.InvalidOperation => "operation failed",
        PlayerDiagnosticCause.Unsupported => "unsupported operation or missing host dependency",
        PlayerDiagnosticCause.CompilerDiagnostic => "compiler diagnostic",
        _ => throw new ArgumentOutOfRangeException(nameof(cause)),
    };
}
