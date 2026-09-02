using System.IO;
using System.Threading;
using System.Windows;
using GfMusicManager.Desktop;
using GfMusicManager.Core.Localization;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class MainWindowStartupTests
{
    [Fact]
    public void MainWindow_CanBeConstructedAndSaveLanguageWithoutMo2Configuration()
    {
        Exception? capturedException = null;
        using var completed = new ManualResetEventSlim();
        var settingsDirectory = Directory.CreateTempSubdirectory("gf-music-language-settings-");
        var settingsPath = Path.Combine(settingsDirectory.FullName, "settings.json");
        var thread = new Thread(() =>
        {
            System.Windows.Application? application = null;
            MainWindow? window = null;
            try
            {
                UiText.SetLanguage(UiLanguage.Japanese);
                application = new System.Windows.Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                var store = new GfMusicManagerSettingsStore(settingsPath);
                window = new MainWindow(store);
                Assert.Null(window.FindName("DfgOutputModeRadioButton"));
                window.SettingsMo2RootTextBox.Text = string.Empty;
                window.LanguageComboBox.SelectedValue = UiLanguage.English;

                var saved = window.TrySaveSettingsFromControls(out var languageChanged);

                Assert.True(saved);
                Assert.True(languageChanged);
                var settings = store.Load();
                Assert.Null(settings.Mo2Root);
                Assert.Null(settings.ProfileName);
                Assert.Equal(UiLanguage.English, settings.Language);
            }
            catch (Exception exception)
            {
                capturedException = exception;
            }
            finally
            {
                window?.Close();
                application?.Shutdown();
                UiText.SetLanguage(UiLanguage.Japanese);
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(completed.Wait(TimeSpan.FromSeconds(10)));
        thread.Join();
        Assert.Null(capturedException);
        settingsDirectory.Delete(recursive: true);
    }

}
