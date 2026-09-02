using System.IO;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;
using GfMusicManager.Desktop;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class MusicSourceDetailsWindowTests
{
    [Fact]
    public void BuildMusicTrackGroups_CollapsesIdenticalTracksAndListsSourcePlugins()
    {
        var firstPlugin = Plugin("AdditionalMusicProject.esp", 10);
        var secondPlugin = Plugin("AdditionalMusicProjectReplacer.esp", 11);
        var firstTrack = Track(
            "003E15:AdditionalMusicProject.esp",
            firstPlugin,
            "ADMPIVExploreDay07",
            @"data\Music\Additional Music Project\ADMP Walking Tall.xwm",
            MusicConditionSource.CreateCurrentTime(8, "GreaterThanOrEqualTo"));
        var secondTrack = Track(
            "003E15:AdditionalMusicProjectReplacer.esp",
            secondPlugin,
            "ADMPIVExploreDay07",
            @"data\Music\Additional Music Project\ADMP Walking Tall.xwm",
            MusicConditionSource.CreateCurrentTime(8, "GreaterThanOrEqualTo"));
        var row = Row(
            @"music\additional music project\admp walking tall.xwm",
            Setting(firstPlugin, firstTrack),
            Setting(secondPlugin, secondTrack));

        var groups = MusicSourceDetailsWindow.BuildMusicTrackGroups(row);

        var group = Assert.Single(groups);
        Assert.Equal("ADMPIVExploreDay07", group.DisplayText);
        Assert.Equal(2, group.SourceDetails.Count);
        Assert.Equal(2, group.SourcePluginNames.Count);
        Assert.Contains(
            UiText.Format(
                "SourceDetails.TrackSourceSummary",
                2,
                string.Join(
                    UiText.Get("Common.ListSeparator"),
                    "AdditionalMusicProject.esp",
                    "AdditionalMusicProjectReplacer.esp")),
            group.DefinitionText);
        Assert.Contains("AdditionalMusicProject.esp", group.DefinitionText);
        Assert.Contains("AdditionalMusicProjectReplacer.esp", group.DefinitionText);
        Assert.Contains(
            UiText.Format("SourceDetails.DefinitionEsp", "AdditionalMusicProject.esp"),
            group.SourceDetails[0].TechnicalText);
        Assert.Contains(
            UiText.Format("SourceDetails.DefinitionEsp", "AdditionalMusicProjectReplacer.esp"),
            group.SourceDetails[1].TechnicalText);
        Assert.Single(group.Conditions);
        Assert.Same(group.Representative, group.SourceDetails[0]);
        Assert.False(group.IsSelected);

        group.IsSelected = true;

        Assert.True(group.IsSelected);
    }

    [Fact]
    public void BuildMusicTrackGroups_KeepsDifferentConditionsAndPathsSeparate()
    {
        var plugin = Plugin("Fixture.esp", 10);
        var morningTrack = Track(
            "000101:Fixture.esp",
            plugin,
            "Track_Forest",
            @"data\Music\Explore\forest.xwm",
            MusicConditionSource.CreateCurrentTime(5, "GreaterThanOrEqualTo"));
        var eveningTrack = Track(
            "000102:Fixture.esp",
            plugin,
            "Track_Forest",
            @"data\Music\Explore\forest.xwm",
            MusicConditionSource.CreateCurrentTime(18, "GreaterThanOrEqualTo"));
        var differentPathTrack = TrackWithPaths(
            "000103:Fixture.esp",
            plugin,
            "Track_Forest",
            new[]
            {
                @"data\Music\Explore\forest.xwm",
                @"data\Music\Explore\mountain.xwm"
            },
            MusicConditionSource.CreateCurrentTime(5, "GreaterThanOrEqualTo"));
        var row = Row(
            @"music\explore\forest.xwm",
            Setting(plugin, morningTrack, eveningTrack, differentPathTrack));

        var groups = MusicSourceDetailsWindow.BuildMusicTrackGroups(row);

        Assert.Equal(3, groups.Count);
        Assert.Equal(3, groups.Count(group => group.SourceDetails.Count == 1));
        Assert.Contains(groups, group => group.AudioText.Contains("mountain.xwm", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(groups, group => group.ConditionsText.Contains("午後6時", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildMusicTrackGroups_DoesNotRepeatTechnicalDetailsForRepeatedTrackReferences()
    {
        var plugin = Plugin("RepeatedReferences.esp", 10);
        var sharedTrack = Track(
            "000201:RepeatedReferences.esp",
            plugin,
            "Track_Repeated",
            @"data\Music\Explore\repeated.xwm");
        var row = Row(
            @"music\explore\repeated.xwm",
            Setting(plugin, sharedTrack),
            Setting(plugin, sharedTrack));

        var groups = MusicSourceDetailsWindow.BuildMusicTrackGroups(row);

        var group = Assert.Single(groups);
        Assert.Single(group.SourceDetails);
        Assert.Single(group.SourcePluginNames);
        Assert.Equal(
            UiText.Format("SourceDetails.DefinitionEsp", "RepeatedReferences.esp"),
            group.DefinitionText);
    }

    [Fact]
    public void InitialConditionEditorUsesSavedGeneratedConditions()
    {
        var sourceCondition = MusicConditionSource.CreateCurrentTime(8, "GreaterThanOrEqualTo");
        var savedCondition = MusicConditionSource.CreateCurrentTime(18, "GreaterThanOrEqualTo");
        var row = new TrackRow(
            "Saved conditions",
            "Fixture",
            "1件",
            TrackAssetHandling.Reference,
            @"music\explore\saved.xwm",
            "ルーズ · music\\explore\\saved.xwm",
            false,
            string.Empty,
            musicConditions: new[] { sourceCondition },
            availableMusicConditions: new[] { sourceCondition });

        row.ReplaceMusicConditions(new[] { savedCondition });

        var conditions = MusicSourceDetailsWindow.GetInitialConditionEditorConditions(row);

        Assert.Equal(new[] { savedCondition }, conditions);
    }

    [Fact]
    public void TrackConditionsAreStoredIndependentlyForEachVisibleTrack()
    {
        var plugin = Plugin("TrackConditions.esp", 10);
        var morning = Track(
            "000301:TrackConditions.esp",
            plugin,
            "Track_Morning",
            @"data\Music\Explore\track-conditions.xwm",
            MusicConditionSource.CreateCurrentTime(5, "GreaterThanOrEqualTo"));
        var night = Track(
            "000302:TrackConditions.esp",
            plugin,
            "Track_Night",
            @"data\Music\Explore\track-conditions.xwm",
            MusicConditionSource.CreateCurrentTime(22, "GreaterThanOrEqualTo"));
        var row = Row(
            @"music\explore\track-conditions.xwm",
            Setting(plugin, morning, night));
        var groups = MusicSourceDetailsWindow.BuildMusicTrackGroups(row);
        Assert.Equal(2, groups.Count);

        var morningEdited = MusicConditionSource.CreateCurrentTime(7, "GreaterThanOrEqualTo");
        var nightEdited = MusicConditionSource.CreateCurrentTime(23, "GreaterThanOrEqualTo");
        row.ReplaceMusicTrackConditions(new[]
        {
            new MusicGenerationTrackPlan(groups[0].SelectionKey, new[] { morningEdited }),
            new MusicGenerationTrackPlan(groups[1].SelectionKey, new[] { nightEdited })
        });

        Assert.Equal(new[] { morningEdited }, row.GetMusicTrackConditions(groups[0].SelectionKey));
        Assert.Equal(new[] { nightEdited }, row.GetMusicTrackConditions(groups[1].SelectionKey));
        Assert.Equal(2, row.GenerationPlanEntry.Tracks.Count);
    }

    private static TrackRow Row(string virtualPath, params MusicSettingSource[] settings)
    {
        var asset = new AssetSource(
            virtualPath,
            AssetSourceKind.Loose,
            "Fixture",
            @"C:\Fixture",
            true,
            $@"C:\Fixture\{virtualPath.Replace('/', '\\')}",
            null,
            12);
        return new TrackRow(
            "fixture",
            "Fixture",
            "1件",
            TrackAssetHandling.Reference,
            virtualPath,
            virtualPath,
            false,
            string.Empty,
            asset,
            settings,
            settings);
    }

    private static MusicSettingSource Setting(
        PluginSource plugin,
        params MusicTrackSource[] tracks)
    {
        var type = Record("000001:" + plugin.Name, plugin, "MusicType", "MUSExploreForest");
        return new MusicSettingSource(
            MusicSettingScope.MusicType,
            type.FormKey,
            type.EditorId,
            type.FormKey,
            type.EditorId,
            type,
            type,
            tracks);
    }

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

    private static MusicTrackSource TrackWithPaths(
        string formKey,
        PluginSource plugin,
        string editorId,
        IReadOnlyList<string> audioPaths,
        params MusicConditionSource[] conditions) =>
        new(
            formKey,
            editorId,
            audioPaths,
            Record(formKey, plugin, "MusicTrack", editorId))
        {
            Conditions = conditions
        };

    private static PluginRecordSource Record(
        string formKey,
        PluginSource plugin,
        string recordType,
        string editorId) =>
        new(formKey, recordType, editorId, false, plugin, true);

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
