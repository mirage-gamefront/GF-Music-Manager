using System.IO;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using GfMusicManager.Desktop;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class MusicTypeManagementWindowTests
{
    [Fact]
    public void BuildTypes_GroupsSameEditorIdAcrossSourcePlugins()
    {
        var firstPlugin = Plugin("AdditionalMusicProject.esp", 10);
        var secondPlugin = Plugin("AdditionalMusicProjectReplacer.esp", 11);
        var firstType = Record("000100:AdditionalMusicProject.esp", firstPlugin);
        var secondType = Record("000100:AdditionalMusicProjectReplacer.esp", secondPlugin);

        var entries = MusicTypeManagementWindow.BuildTypes(
            new[] { Setting(firstType), Setting(secondType) },
            conflicts: null);

        var entry = Assert.Single(entries);
        Assert.Equal("戦闘用（MUSCombat）", entry.DisplayText);
        Assert.Equal(2, entry.SourceFormKeys.Count);
        Assert.Equal(
            UiText.Format(
                "Management.SourceSummaryMany",
                2,
                string.Join(
                    UiText.Get("Common.ListSeparator"),
                    "AdditionalMusicProject.esp",
                    "AdditionalMusicProjectReplacer.esp")),
            entry.RecordSummaryText);
        Assert.Contains(firstType.FormKey, entry.SourceFormKeys);
        Assert.Contains(secondType.FormKey, entry.SourceFormKeys);
        Assert.True(entry.ContainsSourceFormKey(secondType.FormKey));
    }

    [Theory]
    [InlineData(UiLanguage.Japanese)]
    [InlineData(UiLanguage.English)]
    public void BuildTypes_UsesLocalizedSourceSummary(string language)
    {
        try
        {
            UiText.SetLanguage(language);
            var plugin = Plugin("Fixture.esp", 1);
            var musicType = Record("000100:Fixture.esp", plugin);
            var entry = Assert.Single(
                MusicTypeManagementWindow.BuildTypes(
                    new[] { Setting(musicType) },
                    conflicts: null));

            Assert.Equal(
                UiText.Format("Management.SourceSummary", "Fixture.esp"),
                entry.RecordSummaryText);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }

    [Fact]
    public void BuildTypes_CollapsesIdenticalTracksAndKeepsDifferentConditionsSeparate()
    {
        var firstPlugin = Plugin("AdditionalMusicProject.esp", 10);
        var secondPlugin = Plugin("AdditionalMusicProjectReplacer.esp", 11);
        var firstType = Record("000100:AdditionalMusicProject.esp", firstPlugin);
        var secondType = Record("000100:AdditionalMusicProjectReplacer.esp", secondPlugin);
        var firstTrack = Track(
            "003E15:AdditionalMusicProject.esp",
            firstPlugin,
            "ADMPIVExploreDay07",
            "data\\Music\\Additional Music Project\\ADMP Walking Tall.xwm",
            MusicConditionSource.CreateCurrentTime(8, "GreaterThanOrEqualTo"));
        var secondTrack = Track(
            "003E15:AdditionalMusicProjectReplacer.esp",
            secondPlugin,
            "ADMPIVExploreDay07",
            "data\\Music\\Additional Music Project\\ADMP Walking Tall.xwm",
            MusicConditionSource.CreateCurrentTime(8, "GreaterThanOrEqualTo"));
        var differentTrack = Track(
            "003E16:AdditionalMusicProject.esp",
            firstPlugin,
            "ADMPIVExploreDay07",
            "data\\Music\\Additional Music Project\\ADMP Walking Tall.xwm",
            MusicConditionSource.CreateCurrentTime(18, "GreaterThanOrEqualTo"));

        var entries = MusicTypeManagementWindow.BuildTypes(
            new[]
            {
                Setting(firstType, firstTrack, differentTrack),
                Setting(secondType, secondTrack)
            },
            conflicts: null);

        var entry = Assert.Single(entries);
        Assert.Equal(2, entry.Tracks.Count);
        var collapsed = Assert.Single(entry.Tracks.Where(track => track.DisplayText == "ADMPIVExploreDay07" &&
            track.ConditionsText.Contains("8", StringComparison.Ordinal)));
        Assert.Equal(2, collapsed.SourcePluginNames.Count);
        Assert.Contains("AdditionalMusicProject.esp", collapsed.RecordText);
        Assert.Contains("AdditionalMusicProjectReplacer.esp", collapsed.RecordText);
    }

    private static MusicSettingSource Setting(
        PluginRecordSource musicType,
        params MusicTrackSource[] tracks) =>
        new(
            MusicSettingScope.MusicType,
            musicType.FormKey,
            musicType.EditorId,
            musicType.FormKey,
            musicType.EditorId,
            musicType,
            musicType,
            tracks);

    private static MusicTrackSource Track(
        string formKey,
        PluginSource plugin,
        string editorId,
        string audioPath,
        params MusicConditionSource[] conditions) =>
        new(
            formKey,
            editorId,
            new[] { audioPath },
            Record(formKey, plugin, "MusicTrack", editorId))
        {
            Conditions = conditions
        };

    private static PluginRecordSource Record(
        string formKey,
        PluginSource plugin,
        string recordType = "MusicType",
        string editorId = "MUSCombat") =>
        new(formKey, recordType, editorId, false, plugin, true)
        {
            DisplayName = "戦闘用"
        };

    private static PluginSource Plugin(string name, int loadOrder) =>
        new(
            name,
            $@"C:\Fixture\{name}",
            Path.GetFileNameWithoutExtension(name),
            $@"C:\Fixture\{Path.GetFileNameWithoutExtension(name)}",
            true,
            true,
            loadOrder,
            loadOrder);
}
