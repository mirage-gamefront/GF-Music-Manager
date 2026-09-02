using System.IO;
using System.Text.Json;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Localization;

namespace GfMusicManager.Desktop;

internal sealed record GfMusicManagerSettings(
    string? Mo2Root = null,
    string? ProfileName = null,
    bool IncludeDisabledMods = false,
    bool CreateWorldSpaceMusicSettings = false,
    bool EnableFileLogging = false,
    string Language = UiLanguage.Japanese);

internal sealed class GfMusicManagerSettingsStore
{
    private readonly string _settingsPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public GfMusicManagerSettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GF Music Manager",
            "settings.json"))
    {
    }

    internal GfMusicManagerSettingsStore(string settingsPath)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    public GfMusicManagerSettings Load()
    {
        GfMusicManagerLog.Info($"Settings.Load: path={_settingsPath}.");
        try
        {
            if (!File.Exists(_settingsPath))
            {
                GfMusicManagerLog.Info("Settings.Load: file does not exist; using defaults.");
                return new GfMusicManagerSettings();
            }

            var settings = JsonSerializer.Deserialize<GfMusicManagerSettings>(
                               File.ReadAllText(_settingsPath),
                               JsonOptions) ??
                           new GfMusicManagerSettings();
            settings = settings with { Language = UiLanguage.Normalize(settings.Language) };
            GfMusicManagerLog.Info(
                $"Settings.Load: complete. root={settings.Mo2Root ?? "<none>"}, " +
                $"profile={settings.ProfileName ?? "<none>"}, " +
                $"includeDisabled={settings.IncludeDisabledMods}, " +
                $"worldSpaceMusicSettings={settings.CreateWorldSpaceMusicSettings}, " +
                $"fileLogging={settings.EnableFileLogging}, language={settings.Language}.");
            return settings;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            GfMusicManagerLog.Exception("Settings.Load failed; using defaults", exception);
            return new GfMusicManagerSettings();
        }
    }

    public void Save(GfMusicManagerSettings settings)
    {
        GfMusicManagerLog.Info(
            $"Settings.Save: root={settings.Mo2Root ?? "<none>"}, " +
            $"profile={settings.ProfileName ?? "<none>"}, " +
            $"includeDisabled={settings.IncludeDisabledMods}, " +
            $"worldSpaceMusicSettings={settings.CreateWorldSpaceMusicSettings}, " +
            $"fileLogging={settings.EnableFileLogging}, language={settings.Language}.");
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (directory is null)
            {
                return;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            GfMusicManagerLog.Info("Settings.Save: complete.");
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            GfMusicManagerLog.Exception("Settings.Save failed; continuing without persisted settings", exception);
            // Settings are a convenience; a profile scan must not fail because they cannot be saved.
        }
    }
}
