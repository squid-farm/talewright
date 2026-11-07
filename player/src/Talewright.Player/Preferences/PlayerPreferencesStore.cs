using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Talewright.Player.Diagnostics;

namespace Talewright.Player.Preferences;

public enum PlayerTheme
{
    System,
    Light,
    Dark,
}

public enum PlayerReadingWidth
{
    Narrow,
    Normal,
    Wide,
}

public enum PlayerLineSpacing
{
    Compact,
    Normal,
    Relaxed,
}

public enum PlayerReducedMotion
{
    System,
    Reduce,
}

public sealed record PlayerPreferences(
    int Version,
    PlayerTheme Theme,
    int TextSizePercent,
    bool HideMechanicalNotes,
    PlayerReadingWidth ReadingWidth,
    PlayerLineSpacing LineSpacing,
    PlayerReducedMotion ReducedMotion)
{
    public const int CurrentVersion = 1;
    public const int DefaultTextSizePercent = 100;
    public static IReadOnlyList<int> SupportedTextSizes { get; } = Array.AsReadOnly([100, 125, 150, 175, 200]);
    public static IReadOnlyList<PlayerReadingWidth> SupportedReadingWidths { get; } =
        Array.AsReadOnly([PlayerReadingWidth.Narrow, PlayerReadingWidth.Normal, PlayerReadingWidth.Wide]);
    public static IReadOnlyList<PlayerLineSpacing> SupportedLineSpacings { get; } =
        Array.AsReadOnly([PlayerLineSpacing.Compact, PlayerLineSpacing.Normal, PlayerLineSpacing.Relaxed]);
    public static IReadOnlyList<PlayerReducedMotion> SupportedReducedMotions { get; } =
        Array.AsReadOnly([PlayerReducedMotion.System, PlayerReducedMotion.Reduce]);
    public static PlayerPreferences Default { get; } =
        new(CurrentVersion, PlayerTheme.System, DefaultTextSizePercent, false,
            PlayerReadingWidth.Normal, PlayerLineSpacing.Normal, PlayerReducedMotion.System);

    public static bool IsSupportedTextSize(int percent) => SupportedTextSizes.Contains(percent);
}

public sealed class PlayerPreferencesStore(
    IJSRuntime js,
    ILogger<PlayerPreferencesStore> logger,
    PlayerDiagnostics? diagnostics) : IAsyncDisposable
{
    public PlayerPreferencesStore(IJSRuntime js, ILogger<PlayerPreferencesStore> logger)
        : this(js, logger, null) { }

    private const string ModulePath = "./_content/Talewright.Player/Preferences/PlayerPreferences.js";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private IJSObjectReference? _module;
    private bool _initialized;

    public PlayerPreferences Current { get; private set; } = PlayerPreferences.Default;
    public string? Warning { get; private set; }

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _module = await js.InvokeAsync<IJSObjectReference>("import", ModulePath);
        string? value;
        try
        {
            value = await _module.InvokeAsync<string?>("read");
        }
        catch (JSException ex)
        {
            logger.LogError(ex, "Could not read player preferences from browser storage.");
            diagnostics?.Record(PlayerDiagnosticOperation.PreferencesRead, ex);
            Warning = "Settings could not be loaded or saved in this host.";
            value = null;
        }

        Current = Parse(value);
        await _module.InvokeVoidAsync("applyTheme", ThemeValue(Current.Theme));
        await _module.InvokeVoidAsync("applyTextSize", Current.TextSizePercent);
        await _module.InvokeVoidAsync("applyHideMechanicalNotes", Current.HideMechanicalNotes);
        await _module.InvokeVoidAsync("applyReadingWidth", ReadingWidthValue(Current.ReadingWidth));
        await _module.InvokeVoidAsync("applyLineSpacing", LineSpacingValue(Current.LineSpacing));
        await _module.InvokeVoidAsync("applyReducedMotion", ReducedMotionValue(Current.ReducedMotion));
        _initialized = true;
    }

    public async Task SetThemeAsync(PlayerTheme theme)
    {
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown player theme.");
        }
        var module = _module;
        if (!_initialized || module is null)
        {
            throw new InvalidOperationException("Player preferences must be initialized before they can be changed.");
        }

        Current = Current with { Version = PlayerPreferences.CurrentVersion, Theme = theme };
        await module.InvokeVoidAsync("applyTheme", ThemeValue(theme));
        await PersistAsync(module);
    }

    public async Task SetTextSizeAsync(int textSizePercent)
    {
        if (!PlayerPreferences.IsSupportedTextSize(textSizePercent))
        {
            throw new ArgumentOutOfRangeException(nameof(textSizePercent), textSizePercent,
                "Unknown player text size.");
        }
        var module = _module;
        if (!_initialized || module is null)
        {
            throw new InvalidOperationException("Player preferences must be initialized before they can be changed.");
        }

        Current = Current with { Version = PlayerPreferences.CurrentVersion, TextSizePercent = textSizePercent };
        await module.InvokeVoidAsync("applyTextSize", textSizePercent);
        await PersistAsync(module);
    }

    public async Task SetHideMechanicalNotesAsync(bool hide)
    {
        var module = _module;
        if (!_initialized || module is null)
        {
            throw new InvalidOperationException("Player preferences must be initialized before they can be changed.");
        }

        Current = Current with { Version = PlayerPreferences.CurrentVersion, HideMechanicalNotes = hide };
        await module.InvokeVoidAsync("applyHideMechanicalNotes", hide);
        await PersistAsync(module);
    }

    public async Task SetReadingWidthAsync(PlayerReadingWidth readingWidth)
    {
        if (!Enum.IsDefined(readingWidth))
        {
            throw new ArgumentOutOfRangeException(nameof(readingWidth), readingWidth, "Unknown player reading width.");
        }
        var module = _module;
        if (!_initialized || module is null)
        {
            throw new InvalidOperationException("Player preferences must be initialized before they can be changed.");
        }

        Current = Current with { Version = PlayerPreferences.CurrentVersion, ReadingWidth = readingWidth };
        await module.InvokeVoidAsync("applyReadingWidth", ReadingWidthValue(readingWidth));
        await PersistAsync(module);
    }

    public async Task SetLineSpacingAsync(PlayerLineSpacing lineSpacing)
    {
        if (!Enum.IsDefined(lineSpacing))
        {
            throw new ArgumentOutOfRangeException(nameof(lineSpacing), lineSpacing, "Unknown player line spacing.");
        }
        var module = _module;
        if (!_initialized || module is null)
        {
            throw new InvalidOperationException("Player preferences must be initialized before they can be changed.");
        }

        Current = Current with { Version = PlayerPreferences.CurrentVersion, LineSpacing = lineSpacing };
        await module.InvokeVoidAsync("applyLineSpacing", LineSpacingValue(lineSpacing));
        await PersistAsync(module);
    }

    public async Task SetReducedMotionAsync(PlayerReducedMotion reducedMotion)
    {
        if (!Enum.IsDefined(reducedMotion))
        {
            throw new ArgumentOutOfRangeException(nameof(reducedMotion), reducedMotion,
                "Unknown player reduced-motion preference.");
        }
        var module = _module;
        if (!_initialized || module is null)
        {
            throw new InvalidOperationException("Player preferences must be initialized before they can be changed.");
        }

        Current = Current with { Version = PlayerPreferences.CurrentVersion, ReducedMotion = reducedMotion };
        await module.InvokeVoidAsync("applyReducedMotion", ReducedMotionValue(reducedMotion));
        await PersistAsync(module);
    }

    private async Task PersistAsync(IJSObjectReference module)
    {
        try
        {
            var value = JsonSerializer.Serialize(
                new StoredPreferences(PlayerPreferences.CurrentVersion, ThemeValue(Current.Theme),
                    Current.TextSizePercent, Current.HideMechanicalNotes,
                    ReadingWidthValue(Current.ReadingWidth), LineSpacingValue(Current.LineSpacing),
                    ReducedMotionValue(Current.ReducedMotion)),
                JsonOptions);
            await module.InvokeVoidAsync("write", value);
            Warning = null;
        }
        catch (JSException ex)
        {
            logger.LogError(ex, "Could not persist player preferences to browser storage.");
            diagnostics?.Record(PlayerDiagnosticOperation.PreferencesWrite, ex);
            Warning = "These settings will only last for the current session because they could not be saved.";
        }
    }

    private PlayerPreferences Parse(string? value)
    {
        if (value is null)
        {
            return PlayerPreferences.Default;
        }

        StoredPreferences? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredPreferences>(value, JsonOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Stored player preferences are malformed; using defaults.");
            diagnostics?.Record(PlayerDiagnosticOperation.PreferencesRead, ex, warning: true);
            return PlayerPreferences.Default;
        }

        if (stored?.Version != PlayerPreferences.CurrentVersion ||
            stored.Theme is not ("system" or "light" or "dark"))
        {
            logger.LogWarning("Stored player preferences have an unsupported version or theme; using defaults.");
            diagnostics?.Record(PlayerDiagnosticOperation.PreferencesRead, PlayerDiagnosticCause.Unsupported, warning: true);
            return PlayerPreferences.Default;
        }

        var theme = stored.Theme switch
        {
            "light" => PlayerTheme.Light,
            "dark" => PlayerTheme.Dark,
            _ => PlayerTheme.System,
        };
        var textSizePercent = stored.TextSizePercent ?? PlayerPreferences.DefaultTextSizePercent;
        if (!PlayerPreferences.IsSupportedTextSize(textSizePercent))
        {
            logger.LogWarning("Stored player text size is unsupported; using the default size.");
            diagnostics?.Record(PlayerDiagnosticOperation.PreferencesRead, PlayerDiagnosticCause.Unsupported, warning: true);
            textSizePercent = PlayerPreferences.DefaultTextSizePercent;
        }

        var readingWidth = ParseReadingWidth(stored.ReadingWidth);
        var lineSpacing = ParseLineSpacing(stored.LineSpacing);
        var reducedMotion = ParseReducedMotion(stored.ReducedMotion);
        return new(PlayerPreferences.CurrentVersion, theme, textSizePercent,
            stored.HideMechanicalNotes ?? false, readingWidth, lineSpacing, reducedMotion);
    }

    private static string ThemeValue(PlayerTheme theme) => theme switch
    {
        PlayerTheme.System => "system",
        PlayerTheme.Light => "light",
        PlayerTheme.Dark => "dark",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown player theme."),
    };

    private PlayerReadingWidth ParseReadingWidth(string? value) => value switch
    {
        null or "normal" => PlayerReadingWidth.Normal,
        "narrow" => PlayerReadingWidth.Narrow,
        "wide" => PlayerReadingWidth.Wide,
        _ => InvalidReadingWidth(value),
    };

    private PlayerReadingWidth InvalidReadingWidth(string value)
    {
        logger.LogWarning("Stored player reading width '{ReadingWidth}' is unsupported; using the default width.", value);
        diagnostics?.Record(PlayerDiagnosticOperation.PreferencesRead, PlayerDiagnosticCause.Unsupported, warning: true);
        return PlayerReadingWidth.Normal;
    }

    private PlayerLineSpacing ParseLineSpacing(string? value) => value switch
    {
        null or "normal" => PlayerLineSpacing.Normal,
        "compact" => PlayerLineSpacing.Compact,
        "relaxed" => PlayerLineSpacing.Relaxed,
        _ => InvalidLineSpacing(value),
    };

    private PlayerLineSpacing InvalidLineSpacing(string value)
    {
        logger.LogWarning("Stored player line spacing '{LineSpacing}' is unsupported; using the default spacing.", value);
        diagnostics?.Record(PlayerDiagnosticOperation.PreferencesRead, PlayerDiagnosticCause.Unsupported, warning: true);
        return PlayerLineSpacing.Normal;
    }

    private static string ReadingWidthValue(PlayerReadingWidth readingWidth) => readingWidth switch
    {
        PlayerReadingWidth.Narrow => "narrow",
        PlayerReadingWidth.Normal => "normal",
        PlayerReadingWidth.Wide => "wide",
        _ => throw new ArgumentOutOfRangeException(nameof(readingWidth), readingWidth, "Unknown player reading width."),
    };

    private static string LineSpacingValue(PlayerLineSpacing lineSpacing) => lineSpacing switch
    {
        PlayerLineSpacing.Compact => "compact",
        PlayerLineSpacing.Normal => "normal",
        PlayerLineSpacing.Relaxed => "relaxed",
        _ => throw new ArgumentOutOfRangeException(nameof(lineSpacing), lineSpacing, "Unknown player line spacing."),
    };

    private PlayerReducedMotion ParseReducedMotion(string? value) => value switch
    {
        null or "system" => PlayerReducedMotion.System,
        "reduce" => PlayerReducedMotion.Reduce,
        _ => InvalidReducedMotion(value),
    };

    private PlayerReducedMotion InvalidReducedMotion(string value)
    {
        logger.LogWarning("Stored player reduced-motion preference '{ReducedMotion}' is unsupported; using the system preference.", value);
        diagnostics?.Record(PlayerDiagnosticOperation.PreferencesRead, PlayerDiagnosticCause.Unsupported, warning: true);
        return PlayerReducedMotion.System;
    }

    private static string ReducedMotionValue(PlayerReducedMotion reducedMotion) => reducedMotion switch
    {
        PlayerReducedMotion.System => "system",
        PlayerReducedMotion.Reduce => "reduce",
        _ => throw new ArgumentOutOfRangeException(nameof(reducedMotion), reducedMotion,
            "Unknown player reduced-motion preference."),
    };

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    private sealed record StoredPreferences(
        int? Version,
        string? Theme,
        int? TextSizePercent,
        bool? HideMechanicalNotes,
        string? ReadingWidth,
        string? LineSpacing,
        string? ReducedMotion);
}
