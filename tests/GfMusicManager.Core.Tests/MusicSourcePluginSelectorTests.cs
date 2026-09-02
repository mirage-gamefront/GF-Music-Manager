using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicSourcePluginSelectorTests
{
    [Fact]
    public void Select_ExcludesDestinationPluginsFromUnrelatedMods()
    {
        var musicMod = Plugin("Fantasy Music.esp", "Fantasy Music");
        var destinationMod = Plugin(
            "Unofficial Skyrim Special Edition Patch.esp",
            "Unofficial Skyrim Special Edition Patch");

        var names = MusicSourcePluginSelector.Select(
            new[]
            {
                Setting(musicMod),
                Setting(destinationMod)
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Fantasy Music"
            });

        var name = Assert.Single(names);
        Assert.Equal("Fantasy Music.esp", name);
    }

    [Fact]
    public void Select_DeduplicatesAndSortsSourcePlugins()
    {
        var first = Plugin("Zeta Music.esp", "Music Pack");
        var second = Plugin("Alpha Music.esl", "Music Pack");

        var names = MusicSourcePluginSelector.Select(
            new[]
            {
                Setting(first),
                Setting(second),
                Setting(first)
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Music Pack"
            });

        Assert.Equal(new[] { "Alpha Music.esl", "Zeta Music.esp" }, names);
    }

    private static MusicSettingSource Setting(PluginSource plugin)
    {
        var record = new PluginRecordSource(
            $"000001:{plugin.Name}",
            "MusicType",
            "MUSExplore",
            false,
            plugin,
            true);
        var musicType = record with
        {
            FormKey = $"000002:{plugin.Name}",
            EditorId = "MUSExploreType"
        };

        return new MusicSettingSource(
            MusicSettingScope.MusicType,
            record.FormKey,
            record.EditorId,
            musicType.FormKey,
            musicType.EditorId,
            record,
            musicType,
            Array.Empty<MusicTrackSource>());
    }

    private static PluginSource Plugin(string name, string modName) => new(
        name,
        $@"C:\{modName}\{name}",
        modName,
        $@"C:\{modName}",
        true,
        true,
        1,
        1);
}
