using System.IO;
using System.Threading;
using System.Windows.Controls;
using GfMusicManager.Core.Planning;
using GfMusicManager.Desktop;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class MainWindowSelectionTests
{
    [Fact]
    public void TrackGridSelection_SynchronizesOperationSelectionForAddedAndRemovedRows()
    {
        Exception? capturedException = null;
        using var completed = new ManualResetEventSlim();
        var settingsDirectory = Directory.CreateTempSubdirectory("gf-music-selection-settings-");
        var settingsPath = Path.Combine(settingsDirectory.FullName, "settings.json");
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow(new GfMusicManagerSettingsStore(settingsPath));
                var grid = Assert.IsType<DataGrid>(window.FindName("TrackGrid"));
                var first = CreateRow("music\\explore\\first.xwm");
                var second = CreateRow("music\\explore\\second.xwm");
                var third = CreateRow("music\\explore\\third.xwm");

                grid.SelectionMode = DataGridSelectionMode.Extended;
                grid.ItemsSource = new[] { first, second, third };

                grid.SelectedItems.Add(first);
                grid.SelectedItems.Add(second);

                Assert.True(first.IsSelected);
                Assert.True(second.IsSelected);
                Assert.False(third.IsSelected);

                grid.SelectedItems.Remove(first);
                Assert.False(first.IsSelected);
                Assert.True(second.IsSelected);

                grid.SelectedItems.Clear();
                Assert.False(second.IsSelected);
            }
            catch (Exception exception)
            {
                capturedException = exception;
            }
            finally
            {
                window?.Close();
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

    private static TrackRow CreateRow(string virtualPath)
    {
        var asset = new AssetSource(
            virtualPath,
            AssetSourceKind.Loose,
            "Fixture",
            @"C:\Fixture",
            true,
            Path.Combine(@"C:\Fixture", virtualPath),
            null,
            12);
        return TrackRow.FromAsset(asset, generationPlan: new MusicGenerationPlan());
    }
}
