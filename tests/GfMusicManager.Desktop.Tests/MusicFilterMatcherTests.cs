using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using GfMusicManager.Desktop;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class MusicFilterMatcherTests
{
    [Fact]
    public void PlaybackFilters_UseOrWithinTheSameGroup()
    {
        var timeRow = CreateRow(
            musicTypeEditorId: "MUSExplore",
            conditions: new[] { Condition("GetCurrentTime") });
        var weatherRow = CreateRow(
            musicTypeEditorId: "MUSExplore",
            conditions: new[] { Condition("GetIsCurrentWeather") });

        var options = new MusicFilterOptions(new[]
        {
            MusicPlaybackFilterKind.TimeOfDay,
            MusicPlaybackFilterKind.Weather
        });

        Assert.True(MusicFilterMatcher.Matches(timeRow, options));
        Assert.True(MusicFilterMatcher.Matches(weatherRow, options));
    }

    [Fact]
    public void CombatFilterMatchesCombatMusicTypeOrTrackCondition()
    {
        var combatMusicTypeRow = CreateRow("MUSCombat");
        var combatConditionRow = CreateRow(
            "MUSExplore",
            new[] { Condition("GetCombatTargetHasKeyword") });
        var explorationRow = CreateRow("MUSExplore");
        var options = new MusicFilterOptions(new[] { MusicPlaybackFilterKind.Combat });

        Assert.True(MusicFilterMatcher.Matches(combatMusicTypeRow, options));
        Assert.True(MusicFilterMatcher.Matches(combatConditionRow, options));
        Assert.False(MusicFilterMatcher.Matches(explorationRow, options));
    }

    [Fact]
    public void DefinitionQueries_ArePartialMatchAndOrAcrossScopes()
    {
        var row = CreateRow(
            "MUSExplore",
            settings: new[]
            {
                CreateSetting(MusicSettingScope.Cell, "TelMithrynStewardsHouse"),
                CreateSetting(MusicSettingScope.WorldSpace, "SolstheimWorld")
            });

        Assert.True(MusicFilterMatcher.Matches(
            row,
            new MusicFilterOptions(definitionSelections: new[]
            {
                new KeyValuePair<MusicSettingScope, string>(MusicSettingScope.Cell, "Mithryn"),
                new KeyValuePair<MusicSettingScope, string>(MusicSettingScope.WorldSpace, "Solstheim")
            })));

        Assert.False(MusicFilterMatcher.Matches(
            row,
            new MusicFilterOptions(definitionSelections: new[]
            {
                new KeyValuePair<MusicSettingScope, string>(MusicSettingScope.Location, "Whiterun")
            })));
    }

    [Fact]
    public void EmptyDefinitionQueryMatchesAllRows()
    {
        var row = CreateRow(
            "MUSExplore",
            settings: new[] { CreateSetting(MusicSettingScope.Cell, "CellFixture") });

        Assert.True(MusicFilterMatcher.Matches(
            row,
            new MusicFilterOptions(definitionSelections: new[]
            {
                new KeyValuePair<MusicSettingScope, string>(MusicSettingScope.Cell, " ")
            })));
    }

    [Fact]
    public void NoConditionFilterUsesTrackConditionsOnly()
    {
        var row = CreateRow("MUSExplore");
        var options = new MusicFilterOptions(new[] { MusicPlaybackFilterKind.NoCondition });

        Assert.True(MusicFilterMatcher.Matches(row, options));
    }

    [Fact]
    public void DefinitionSelectionWithBlankQueryMatchesAnyRecordInThatScope()
    {
        var rowWithCell = CreateRow(
            "MUSExplore",
            settings: new[] { CreateSetting(MusicSettingScope.Cell, "CellFixture") });
        var rowWithoutCell = CreateRow("MUSExplore");
        var options = new MusicFilterOptions(
            definitionSelections: new[]
            {
                new KeyValuePair<MusicSettingScope, string>(MusicSettingScope.Cell, string.Empty)
            });

        Assert.True(MusicFilterMatcher.Matches(rowWithCell, options));
        Assert.False(MusicFilterMatcher.Matches(rowWithoutCell, options));
    }

    [Fact]
    public void CandidateCatalogProvidesDataDrivenCombatTimeAndWeatherSelections()
    {
        var combat = Condition("GetCombatTargetHasKeyword") with
        {
            KeywordFormKey = "000001:Skyrim.esm",
            KeywordEditorId = "ActorTypeDragon",
            KeywordJapaneseExplanation = "ドラゴン"
        };
        var timeStart = MusicConditionSource.CreateCurrentTime(5, "GreaterThanOrEqualTo");
        var timeEnd = MusicConditionSource.CreateCurrentTime(8, "LessThan");
        var weather = Condition("GetIsCurrentWeather") with
        {
            WeatherFormKey = "000002:Skyrim.esm",
            WeatherEditorId = "SkyrimWeatherRain",
            WeatherJapaneseExplanation = "雨"
        };
        var row = CreateRow(
            "MUSExplore",
            new[] { combat, timeStart, timeEnd, weather });

        var candidates = MusicFilterCandidates.FromTracks(new[] { row });
        var combatCandidate = Assert.Single(candidates.Combat.Skip(1));
        var timeCandidate = Assert.Single(candidates.TimeOfDay.Skip(1));
        var weatherCandidate = Assert.Single(candidates.Weather.Skip(1));

        Assert.Contains("ActorTypeDragon", combatCandidate.DisplayText);
        Assert.Contains("ドラゴン", combatCandidate.DisplayText);
        Assert.Contains("朝", timeCandidate.DisplayText);
        Assert.Contains("雨", weatherCandidate.DisplayText);
        Assert.True(MusicFilterMatcher.Matches(
            row,
            new MusicFilterOptions(new[]
            {
                new KeyValuePair<MusicPlaybackFilterKind, string>(
                    MusicPlaybackFilterKind.Combat,
                    combatCandidate.Key)
            })));
        Assert.True(MusicFilterMatcher.Matches(
            row,
            new MusicFilterOptions(new[]
            {
                new KeyValuePair<MusicPlaybackFilterKind, string>(
                    MusicPlaybackFilterKind.TimeOfDay,
                    timeCandidate.Key)
            })));
    }

    [Theory]
    [InlineData(UiLanguage.Japanese)]
    [InlineData(UiLanguage.English)]
    public void CandidateCatalogUsesLocalizedCategoryLabels(string language)
    {
        try
        {
            UiText.SetLanguage(language);
            var candidates = MusicFilterCandidates.FromTracks(new[] { CreateRow("MUSExplore") });

            Assert.Equal(UiText.Get("Filter.All"), candidates.Combat[0].DisplayText);
            Assert.Equal(
                UiText.Format("Filter.AllCategory", UiText.Get("Filter.Combat")),
                candidates.Combat[0].DetailText);
            Assert.Equal(UiText.Get("Filter.All"), candidates.Weather[0].DisplayText);
            Assert.Equal(
                UiText.Format("Filter.AllCategory", UiText.Get("Filter.Weather")),
                candidates.Weather[0].DetailText);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }

    [Fact]
    public void TimeSelectionMatchesOneRangeWhenTrackHasMultipleRanges()
    {
        var row = CreateRow(
            "MUSExplore",
            new[]
            {
                MusicConditionSource.CreateCurrentTime(5, "GreaterThanOrEqualTo"),
                MusicConditionSource.CreateCurrentTime(8, "LessThan"),
                MusicConditionSource.CreateCurrentTime(18, "GreaterThanOrEqualTo"),
                MusicConditionSource.CreateCurrentTime(22, "LessThan")
            });
        var candidates = MusicFilterCandidates.FromTracks(new[] { row });
        var morning = Assert.Single(candidates.TimeOfDay.Skip(1).Where(
            candidate => candidate.DisplayText.Contains("朝", StringComparison.Ordinal)));

        Assert.True(MusicFilterMatcher.Matches(
            row,
            new MusicFilterOptions(new[]
            {
                new KeyValuePair<MusicPlaybackFilterKind, string>(
                    MusicPlaybackFilterKind.TimeOfDay,
                    morning.Key)
            })));
    }

    [Fact]
    public void CandidateCatalogUsesScannedKeywordAndWeatherCandidates()
    {
        var row = CreateRow(
            "MUSExplore",
            keywordRecords: new[]
            {
                CreateCandidateRecord("000003:Fixture.esp", "Keyword", "ActorTypeDragon"),
                CreateCandidateRecord("000004:Fixture.esp", "Keyword", "ActorTypeUndead")
            },
            weatherRecords: new[]
            {
                CreateCandidateRecord("000005:Fixture.esp", "Weather", "SkyrimWeatherRain")
            });

        var candidates = MusicFilterCandidates.FromTracks(new[] { row });
        var combatLabels = candidates.Combat.Skip(1).Select(candidate => candidate.DisplayText).ToArray();
        var weatherLabels = candidates.Weather.Skip(1).Select(candidate => candidate.DisplayText).ToArray();

        Assert.Contains(combatLabels, label => label.Contains("ActorTypeDragon", StringComparison.Ordinal));
        Assert.Contains(combatLabels, label => label.Contains("ActorTypeUndead", StringComparison.Ordinal));
        Assert.Contains(combatLabels, label => label.Contains("ドラゴン", StringComparison.Ordinal));
        Assert.Contains(combatLabels, label => label.Contains("アンデッド", StringComparison.Ordinal));
        Assert.Contains(weatherLabels, label => label.Contains("SkyrimWeatherRain", StringComparison.Ordinal));
        Assert.Contains(weatherLabels, label => label.Contains("雨", StringComparison.Ordinal));
        Assert.DoesNotContain(combatLabels, label => label.Contains("を持つ", StringComparison.Ordinal));
        Assert.DoesNotContain(weatherLabels, label => label.Contains("一致", StringComparison.Ordinal));
    }

    private static TrackRow CreateRow(
        string musicTypeEditorId,
        IReadOnlyList<MusicConditionSource>? conditions = null,
        IReadOnlyList<MusicSettingSource>? settings = null,
        IReadOnlyList<PluginRecordSource>? keywordRecords = null,
        IReadOnlyList<PluginRecordSource>? weatherRecords = null)
    {
        return new TrackRow(
            "Fixture Track",
            "Fixture Mod",
            "定義元",
            TrackAssetHandling.Reference,
            @"music\fixture.xwm",
            "ルーズ · music\\fixture.xwm",
            false,
            string.Empty,
            musicSettings: settings ?? new[] { CreateSetting(MusicSettingScope.MusicType, musicTypeEditorId) },
            musicConditions: conditions ?? Array.Empty<MusicConditionSource>(),
            availableKeywordRecords: keywordRecords,
            availableWeatherRecords: weatherRecords);
    }

    private static MusicSettingSource CreateSetting(MusicSettingScope scope, string editorId)
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
            $"000001:{plugin.Name}",
            scope.ToString(),
            editorId,
            false,
            plugin);
        var musicType = new PluginRecordSource(
            $"000002:{plugin.Name}",
            "MusicType",
            scope == MusicSettingScope.MusicType ? editorId : "MUSExplore",
            false,
            plugin);
        return new MusicSettingSource(
            scope,
            record.FormKey,
            record.EditorId,
            musicType.FormKey,
            musicType.EditorId,
            record,
            musicType,
            Array.Empty<MusicTrackSource>());
    }

    private static MusicConditionSource Condition(string functionName) => new(
        functionName,
        "EqualTo",
        1,
        string.Empty,
        functionName,
        string.Empty);

    private static PluginRecordSource CreateCandidateRecord(
        string formKey,
        string recordType,
        string editorId)
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
        return new PluginRecordSource(formKey, recordType, editorId, false, plugin);
    }
}
