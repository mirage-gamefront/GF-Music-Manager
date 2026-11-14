using System.IO;
using GfMusicManager.Core.Localization;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class GfMusicManagerSettingsStoreTests
{
    [Fact]
    public void MissingSettingsDefaultFileLoggingToOff()
    {
        using var fixture = new SettingsFixture();

        var settings = fixture.Store.Load();

        Assert.False(settings.EnableFileLogging);
        Assert.Equal(GfMusicManagerSettingsDefaults.PreviewVolume, settings.PreviewVolume);
        Assert.False(File.Exists(fixture.SettingsPath));
    }

    [Fact]
    public void MissingSettingsLeaveTheLanguageUnchosenForTheFirstLaunchDialog()
    {
        using var fixture = new SettingsFixture();

        Assert.Null(fixture.Store.Load().Language);
    }

    [Fact]
    public void LanguageChosenAtFirstLaunchIsKept()
    {
        using var fixture = new SettingsFixture();
        fixture.Store.Save(fixture.Store.Load() with { Language = UiLanguage.English });

        Assert.Equal(UiLanguage.English, fixture.Store.Load().Language);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveAndLoadPersistsFileLogging(bool enabled)
    {
        using var fixture = new SettingsFixture();
        fixture.Store.Save(new GfMusicManagerSettings(EnableFileLogging: enabled));

        var loaded = fixture.Store.Load();

        Assert.Equal(enabled, loaded.EnableFileLogging);
    }

    [Fact]
    public void ExistingSettingsWithoutFileLoggingDefaultToOff()
    {
        using var fixture = new SettingsFixture();
        File.WriteAllText(
            fixture.SettingsPath,
            """
            {
              "Mo2Root": "C:\\MO2",
              "ProfileName": "Main",
              "IncludeDisabledMods": true,
              "CreateWorldSpaceMusicSettings": true
            }
            """);

        var loaded = fixture.Store.Load();

        Assert.False(loaded.EnableFileLogging);
        Assert.Equal(UiLanguage.Japanese, loaded.Language);
        Assert.Equal(GfMusicManagerSettingsDefaults.PreviewVolume, loaded.PreviewVolume);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(56)]
    [InlineData(100)]
    public void SaveAndLoadPersistsPreviewVolume(double volume)
    {
        using var fixture = new SettingsFixture();
        fixture.Store.Save(new GfMusicManagerSettings(PreviewVolume: volume));

        var loaded = fixture.Store.Load();

        Assert.Equal(volume, loaded.PreviewVolume);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(101, 100)]
    public void LoadClampsPreviewVolume(double stored, double expected)
    {
        using var fixture = new SettingsFixture();
        fixture.Store.Save(new GfMusicManagerSettings(PreviewVolume: stored));

        var loaded = fixture.Store.Load();

        Assert.Equal(expected, loaded.PreviewVolume);
    }

    [Theory]
    [InlineData(UiLanguage.Japanese)]
    [InlineData(UiLanguage.English)]
    public void SaveAndLoadPersistsSupportedLanguage(string language)
    {
        using var fixture = new SettingsFixture();
        fixture.Store.Save(new GfMusicManagerSettings(Language: language));

        var loaded = fixture.Store.Load();

        Assert.Equal(language, loaded.Language);
    }

    [Fact]
    public void UnsupportedLanguageFallsBackToJapanese()
    {
        using var fixture = new SettingsFixture();
        fixture.Store.Save(new GfMusicManagerSettings(Language: "invalid"));

        var loaded = fixture.Store.Load();

        Assert.Equal(UiLanguage.Japanese, loaded.Language);
    }

    private sealed class SettingsFixture : IDisposable
    {
        private readonly DirectoryInfo _directory =
            Directory.CreateTempSubdirectory("gf-music-settings-");

        public SettingsFixture()
        {
            SettingsPath = Path.Combine(_directory.FullName, "settings.json");
            Store = new GfMusicManagerSettingsStore(SettingsPath);
        }

        public string SettingsPath { get; }

        public GfMusicManagerSettingsStore Store { get; }

        public void Dispose()
        {
            try
            {
                _directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Temporary test data can be collected by the OS later.
            }
        }
    }
}
