using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Talewright.Player.Preferences;

public enum PlayerTheme
{
    System,
    Light,
    Dark,
}

public sealed record PlayerPreferences(int Version, PlayerTheme Theme, int TextSizePercent, bool HideMechanicalNotes)
{
    public const int CurrentVersion = 1;
    public const int DefaultTextSizePercent = 100;
    public static IReadOnlyList<int> SupportedTextSizes { get; } = Array.AsReadOnly([100, 125, 150, 175, 200]);
    public static PlayerPreferences Default { get; } =
        new(CurrentVersion, PlayerTheme.System, DefaultTextSizePercent, false);

    public static bool IsSupportedTextSize(int percent) => SupportedTextSizes.Contains(percent);
}

public sealed class PlayerPreferencesStore(
    IJSRuntime js,
    ILogger<PlayerPreferencesStore> logger) : IAsyncDisposable
{
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
            Warning = "Settings could not be loaded or saved in this host.";
            value = null;
        }

        Current = Parse(value);
        await _module.InvokeVoidAsync("applyTheme", ThemeValue(Current.Theme));
        await _module.InvokeVoidAsync("applyTextSize", Current.TextSizePercent);
        await _module.InvokeVoidAsync("applyHideMechanicalNotes", Current.HideMechanicalNotes);
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

    private async Task PersistAsync(IJSObjectReference module)
    {
        try
        {
            var value = JsonSerializer.Serialize(
                new StoredPreferences(PlayerPreferences.CurrentVersion, ThemeValue(Current.Theme),
                    Current.TextSizePercent, Current.HideMechanicalNotes),
                JsonOptions);
            await module.InvokeVoidAsync("write", value);
            Warning = null;
        }
        catch (JSException ex)
        {
            logger.LogError(ex, "Could not persist player preferences to browser storage.");
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
            return PlayerPreferences.Default;
        }

        if (stored?.Version != PlayerPreferences.CurrentVersion ||
            stored.Theme is not ("system" or "light" or "dark"))
        {
            logger.LogWarning("Stored player preferences have an unsupported version or theme; using defaults.");
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
            textSizePercent = PlayerPreferences.DefaultTextSizePercent;
        }

        return new(PlayerPreferences.CurrentVersion, theme, textSizePercent,
            stored.HideMechanicalNotes ?? false);
    }

    private static string ThemeValue(PlayerTheme theme) => theme switch
    {
        PlayerTheme.System => "system",
        PlayerTheme.Light => "light",
        PlayerTheme.Dark => "dark",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unknown player theme."),
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
        int? Version, string? Theme, int? TextSizePercent, bool? HideMechanicalNotes);
}
