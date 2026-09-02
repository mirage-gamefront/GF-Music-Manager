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
        Assert.False(File.Exists(fixture.SettingsPath));
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
