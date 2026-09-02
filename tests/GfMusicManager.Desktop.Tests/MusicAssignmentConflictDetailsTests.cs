using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;
using GfMusicManager.Desktop;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class MusicAssignmentConflictDetailsTests
{
    [Theory]
    [InlineData(UiLanguage.Japanese)]
    [InlineData(UiLanguage.English)]
    public void Create_ListsTargetMusicTypesAndNonWorldSpaceHandling(string language)
    {
        try
        {
            UiText.SetLanguage(language);
            var plugin = new PluginSource(
                "Fixture.esp",
                @"C:\Fixture\Fixture.esp",
                "Fixture",
                @"C:\Fixture",
                true,
                true,
                1,
                1);
            var cellRecord = Record(
                "000010:Fixture.esp",
                "Cell",
                "Cell_Fixture",
                plugin);
            var firstTypeRecord = Record(
                "000011:Fixture.esp",
                "MusicType",
                "MUSExplore",
                plugin);
            var secondTypeRecord = Record(
                "000012:Fixture.esp",
                "MusicType",
                "MUSCombat",
                plugin);
            var firstSetting = Setting(cellRecord, firstTypeRecord);
            var secondSetting = Setting(cellRecord, secondTypeRecord);
            var plan = new MusicGenerationPlan();
            plan.GetOrCreate(Asset("Mod A", @"music\first.xwm"), new[] { firstSetting });
            plan.GetOrCreate(Asset("Mod B", @"music\second.xwm"), new[] { secondSetting });

            var conflict = Assert.Single(plan.Conflicts);
            var view = AssignmentConflictView.Create(
                conflict,
                new[] { firstSetting, secondSetting },
                keepVanillaMusic: false);

            Assert.Contains("Cell_Fixture", view.TargetText);
            Assert.Equal(UiText.Format("Assignment.MusicTypeHeading", 2), view.MusicTypeHeadingText);
            Assert.Contains("GFITG_C_Cell_Fixture", view.IntegratedMusicTypeText);
            Assert.Contains(
                UiText.Format("Assignment.IntegratedTrack", 2, 0, 2),
                view.IntegratedTrackText);
            Assert.Equal(2, view.Assignments.Count);
            Assert.Contains(view.Assignments, assignment => assignment.MusicTypeText.Contains("MUSExplore"));
            Assert.Contains(view.Assignments, assignment => assignment.MusicTypeText.Contains("MUSCombat"));
            Assert.All(
                view.Assignments,
                assignment => Assert.Equal(UiText.Format("Assignment.AdoptedTracks", 1), assignment.AdoptedTrackText));
            Assert.Contains(view.Assignments, assignment => assignment.SourceModsText.Contains("Mod A"));
            Assert.Contains(view.Assignments, assignment => assignment.SourceModsText.Contains("Mod B"));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }

    private static MusicSettingSource Setting(
        PluginRecordSource scopeRecord,
        PluginRecordSource musicTypeRecord) => new(
        MusicSettingScope.Cell,
        scopeRecord.FormKey,
        scopeRecord.EditorId,
        musicTypeRecord.FormKey,
        musicTypeRecord.EditorId,
        scopeRecord,
        musicTypeRecord,
        Array.Empty<MusicTrackSource>());

    private static PluginRecordSource Record(
        string formKey,
        string recordType,
        string editorId,
        PluginSource plugin) => new(
        formKey,
        recordType,
        editorId,
        false,
        plugin,
        true);

    private static AssetSource Asset(string modName, string virtualPath) => new(
        virtualPath,
        AssetSourceKind.Loose,
        modName,
        $@"C:\Fixture\{modName}",
        true,
        $@"C:\Fixture\{modName}\{virtualPath.Replace('/', '\\')}",
        null,
        12);
}
