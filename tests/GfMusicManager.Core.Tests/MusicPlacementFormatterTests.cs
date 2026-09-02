using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicPlacementFormatterTests
{
    [Fact]
    public void Format_GroupsMusicTypesInsteadOfRepeatingEachRecord()
    {
        var settings = new[]
        {
            Setting(MusicSettingScope.MusicType, "MUSCombat"),
            Setting(MusicSettingScope.MusicType, "MUSCombatCivilWar"),
            Setting(MusicSettingScope.MusicType, "MUSCombatBoss"),
            Setting(MusicSettingScope.MusicType, "MUSCombat")
        };

        var text = MusicPlacementFormatter.Format(settings);

        Assert.Equal(
            UiText.Format(
                "Analysis.Placement.Group",
                UiText.Get("Scope.MusicType"),
                UiText.Format("Analysis.Placement.MoreCount", "MUSCombat, MUSCombatBoss", 1)),
            text);
        Assert.DoesNotContain(
            $"{UiText.Get("Scope.MusicType")} / MUSCombat / {UiText.Get("Scope.MusicType")}",
            text);
    }

    [Fact]
    public void Format_PrefersReadableScopeSummaryWhenScopeUsagesExist()
    {
        var settings = new[]
        {
            Setting(MusicSettingScope.MusicType, "MUSExplore"),
            Setting(MusicSettingScope.WorldSpace, "Tamriel"),
            Setting(MusicSettingScope.Cell, "Cell_A"),
            Setting(MusicSettingScope.Cell, "Cell_B"),
            Setting(MusicSettingScope.Cell, "Cell_C"),
            Setting(MusicSettingScope.Cell, "Cell_D")
        };

        var text = MusicPlacementFormatter.Format(settings);

        Assert.Equal(
            string.Join(
                UiText.Get("Analysis.Placement.SummarySeparator"),
                UiText.Format("Analysis.Placement.Group", UiText.Get("Scope.WorldSpace"), "Tamriel"),
                UiText.Format(
                    "Analysis.Placement.Group",
                    UiText.Get("Scope.Cell"),
                    UiText.Format("Analysis.Placement.MoreCount", "Cell_A", 3))),
            text);
        Assert.DoesNotContain($"{UiText.Get("Scope.MusicType")} /", text);
        Assert.DoesNotContain($"/ {UiText.Get("Scope.WorldSpace")} /", text);
    }

    [Fact]
    public void FormatDetailed_GroupsUniqueNamesByScope()
    {
        var settings = new[]
        {
            Setting(MusicSettingScope.MusicType, "MUSCombat"),
            Setting(MusicSettingScope.MusicType, "MUSCombat"),
            Setting(MusicSettingScope.Cell, "Cell_A"),
            Setting(MusicSettingScope.Cell, "Cell_A"),
            Setting(MusicSettingScope.WorldSpace, "Tamriel")
        };

        var text = MusicPlacementFormatter.FormatDetailed(settings);

        Assert.Contains(
            UiText.Format(
                "Analysis.Placement.DetailedGroup",
                UiText.Get("Scope.MusicType"),
                1,
                "MUSCombat"),
            text);
        Assert.Contains(
            UiText.Format(
                "Analysis.Placement.DetailedGroup",
                UiText.Get("Scope.WorldSpace"),
                1,
                "Tamriel"),
            text);
        Assert.Contains(
            UiText.Format(
                "Analysis.Placement.DetailedGroup",
                UiText.Get("Scope.Cell"),
                1,
                "Cell_A"),
            text);
    }

    [Fact]
    public void FormatCount_ReturnsOnlyTheNumberOfSettings()
    {
        var settings = new[]
        {
            Setting(MusicSettingScope.Cell, "Cell_A"),
            Setting(MusicSettingScope.Cell, "Cell_B"),
            Setting(MusicSettingScope.WorldSpace, "Tamriel")
        };

        Assert.Equal("3件", MusicPlacementFormatter.FormatCount(settings));
    }

    [Fact]
    public void ScopeNameFormatter_PreservesTechnicalNameAlongsideReadableName()
    {
        Assert.Equal(
            "リバーウッド（RiverwoodLocation）",
            MusicScopeNameFormatter.Format(
                MusicSettingScope.Location,
                "RiverwoodLocation",
                "リバーウッド"));
        Assert.Equal(
            "名称なしの屋外セル（Wilderness）",
            MusicScopeNameFormatter.Format(MusicSettingScope.Cell, "Wilderness"));
        Assert.Equal(
            "名称なしの屋外セル（TamrielExteriorCell）",
            MusicScopeNameFormatter.Format(
                MusicSettingScope.Cell,
                "TamrielExteriorCell",
                "Wilderness"));
        Assert.Equal(
            "戦闘用音楽タイプ（MUSCombat）",
            MusicScopeNameFormatter.Format(MusicSettingScope.MusicType, "MUSCombat"));
    }

    [Theory]
    [InlineData("MUSDwemer", "ドゥーマー遺跡用音楽タイプ（MUSDwemer）")]
    [InlineData("MUSTownMarkarth", "マルカルスの街用音楽タイプ（MUSTownMarkarth）")]
    [InlineData("MUSTownRiften", "リフテンの街用音楽タイプ（MUSTownRiften）")]
    [InlineData("MUSExploreDLCSoulCairn", "ソウル・ケルン探索用音楽タイプ（MUSExploreDLCSoulCairn）")]
    [InlineData("MUSDungeonDLCVampireCastle", "ヴォルキハル城ダンジョン用音楽タイプ（MUSDungeonDLCVampireCastle）")]
    [InlineData("DLC2MUSCombatKarstaag", "カルストーグ戦闘用音楽タイプ（DLC2MUSCombatKarstaag）")]
    [InlineData("DLC2MUSDungeonApocrypha", "アポクリファ用音楽タイプ（DLC2MUSDungeonApocrypha）")]
    [InlineData("MUSExploreForestPine", "針葉樹林探索用音楽タイプ（MUSExploreForestPine）")]
    public void ScopeNameFormatter_UsesMusicTypeExplanationWhenLocalizedNameIsMissing(
        string technicalName,
        string expected)
    {
        Assert.Equal(
            expected,
            MusicScopeNameFormatter.Format(MusicSettingScope.MusicType, technicalName));
    }

    [Theory]
    [InlineData("FallowstoneCaveExit", "ファロウストーン洞窟（出口）（FallowstoneCaveExit）")]
    [InlineData("MossMotherCavernStart", "モス・マザー洞窟（入口）（MossMotherCavernStart）")]
    [InlineData("DeepwoodRedoubtStart", "ディープウッド砦（入口）（DeepwoodRedoubtStart）")]
    [InlineData("DarkwaterCavernW01", "ダークウォーター洞窟（区画01）（DarkwaterCavernW01）")]
    [InlineData("MazeStart", "シャリドールの迷宮（入口）（MazeStart）")]
    public void ScopeNameFormatter_UsesCellExplanationAndKeepsTechnicalName(
        string technicalName,
        string expected)
    {
        Assert.Equal(
            expected,
            MusicScopeNameFormatter.Format(MusicSettingScope.Cell, technicalName));
    }

    [Fact]
    public void ScopeNameFormatter_UsesScopeSpecificExplanationForRegionAndWorldSpace()
    {
        Assert.Equal(
            "ツンドラ地帯（WeatherTundra）",
            MusicScopeNameFormatter.Format(MusicSettingScope.Region, "WeatherTundra"));
        Assert.Equal(
            "ウィンドヘルムの闘技場ワールド（WindhelmPitWorldspace）",
            MusicScopeNameFormatter.Format(MusicSettingScope.WorldSpace, "WindhelmPitWorldspace"));
    }

    [Theory]
    [InlineData("MorthalExterior05", "モーサル（外周区画05）（MorthalExterior05）")]
    [InlineData("LabyrinthianBossChamber", "ラビリンシアン（ボス部屋）（LabyrinthianBossChamber）")]
    [InlineData("DLC1zFalmerValley02", "忘れられた谷（区画02）（DLC1zFalmerValley02）")]
    [InlineData("FallowstoneCaveStartNew", "ファロウストーン洞窟（入口）（FallowstoneCaveStartNew）")]
    public void ScopeNameFormatter_UsesEditorIdPlaceAndSuffixTokens(
        string technicalName,
        string expected)
    {
        Assert.Equal(
            expected,
            MusicScopeNameFormatter.Format(MusicSettingScope.Cell, technicalName));
    }

    [Fact]
    public void ScopeNameFormatter_ReplacesEnglishDisplayNameWhenAnEditorIdExplanationExists()
    {
        Assert.Equal(
            "DLC1テスト用植物ワールド（TestMeganWorld）",
            MusicScopeNameFormatter.Format(
                MusicSettingScope.WorldSpace,
                "TestMeganWorld",
                "TestDLC1PlantWorld"));
        Assert.Equal(
            "フロストリバー農場（FrostRiverFarmLocation）",
            MusicScopeNameFormatter.Format(
                MusicSettingScope.Location,
                "FrostRiverFarmLocation"));
    }

    [Fact]
    public void ScopeNameFormatter_UsesEnglishCatalogWithoutJapaneseInference()
    {
        var previousLanguage = UiText.Language;
        try
        {
            UiText.SetLanguage(UiLanguage.English);

            Assert.Equal(
                "TestDLC1PlantWorld (TestMeganWorld)",
                MusicScopeNameFormatter.Format(
                    MusicSettingScope.WorldSpace,
                    "TestMeganWorld",
                    "TestDLC1PlantWorld"));
            Assert.Equal(
                "FrostRiverFarmLocation",
                MusicScopeNameFormatter.Format(
                    MusicSettingScope.Location,
                    "FrostRiverFarmLocation"));
            Assert.Equal("Music Type", MusicScopeNameFormatter.GetJapaneseLabel(MusicSettingScope.MusicType));
        }
        finally
        {
            UiText.SetLanguage(previousLanguage);
        }
    }

    [Fact]
    public void ScopeNameFormatter_CanAvoidRepeatingMusicTypeSuffixInPrefixedLabels()
    {
        Assert.Equal(
            "探索用（MUSExploreForest）",
            MusicScopeNameFormatter.WithoutMusicTypeSuffix(
                "探索用音楽タイプ（MUSExploreForest）"));
        Assert.Equal(
            "未設定（_NONE）",
            MusicScopeNameFormatter.WithoutMusicTypeSuffix("未設定（_NONE）"));
    }

    [Fact]
    public void ScanProgressFormatter_UsesJapaneseStageAndCurrentTotal()
    {
        Assert.Equal(
            "MOD 37 / 137",
            MusicScanProgressFormatter.Format(
                new ScanProgress(ScanIssueSeverity.Info, "MOD", "Scanning", 37, 137)));
        Assert.Equal(
            "プラグイン 24 / 57",
            MusicScanProgressFormatter.Format(
                new ScanProgress(ScanIssueSeverity.Info, "Plugin", "Reading", 24, 57)));
    }

    [Fact]
    public void ScanProgressFormatter_ShowsCurrentModAndPluginNames()
    {
        Assert.Equal(
            "MOD：Fantasy Music Pack　・　プラグイン：Fantasy Music Pack.esp",
            MusicScanProgressFormatter.Format(
                new ScanProgress(
                    ScanIssueSeverity.Info,
                    "Plugin",
                    "Reading Fantasy Music Pack.esp",
                    24,
                    57,
                    "Fantasy Music Pack",
                    @"C:\Fixture\Fantasy Music Pack.esp",
                    "Fantasy Music Pack.esp")));
    }

    private static MusicSettingSource Setting(MusicSettingScope scope, string name)
    {
        var plugin = new PluginSource(
            "Fixture.esp",
            @"C:\Fixture\Fixture.esp",
            "Fixture",
            @"C:\Fixture",
            true,
            true,
            1,
            1);
        var record = new PluginRecordSource(
            $"000001:Fixture.esp:{scope}:{name}",
            scope == MusicSettingScope.MusicType ? "MusicType" : scope.ToString(),
            name,
            false,
            plugin,
            true);
        return new MusicSettingSource(
            scope,
            record.FormKey,
            name,
            "000001:Fixture.esp",
            "MUSFixture",
            record,
            record,
            Array.Empty<MusicTrackSource>());
    }
}
