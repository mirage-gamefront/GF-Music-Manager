using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicConditionFormatterTests
{
    [Fact]
    public void Format_UsesReadableJapaneseNameAndComparisonSymbol()
    {
        var condition = new MusicConditionSource(
            "GetCurrentTime",
            "GreaterThanOrEqualTo",
            5f,
            string.Empty,
            "GetCurrentTimeConditionData",
            string.Empty);

        var expectedTime = UiText.Format(
            "Analysis.Condition.Time.AfterOrAt",
            UiText.Format("Analysis.Condition.Clock.Hour", UiText.Get("Analysis.Condition.Clock.Am"), 5));
        Assert.Equal(
            UiText.Format("Analysis.Condition.TimeOfDay", expectedTime),
            MusicConditionFormatter.Format(condition));
        Assert.Contains("GetCurrentTime GreaterThanOrEqualTo", MusicConditionFormatter.FormatTechnical(condition));
    }

    [Fact]
    public void Format_HidesTechnicalDataFromTheMainDisplayButKeepsOrMarker()
    {
        var condition = new MusicConditionSource(
            "UnknownFunction",
            "LessThanOrEqualTo",
            22f,
            "OR",
            "UnknownConditionData",
            "Param=Example");

        var expected = UiText.Format(
            "Analysis.Condition.Numeric",
            UiText.Get("Analysis.Condition.Function.Other"),
            "≤",
            "22") + UiText.Get("Analysis.Condition.AnyOfGroup");
        Assert.Equal(expected, MusicConditionFormatter.Format(condition));
        Assert.Contains("Param=Example", MusicConditionFormatter.FormatTechnical(condition));
    }

    [Fact]
    public void Format_UsesReadableNameForCombatKeywordCondition()
    {
        var condition = new MusicConditionSource(
            "GetCombatTargetHasKeyword",
            "EqualTo",
            1f,
            string.Empty,
            "GetCombatTargetHasKeywordConditionData",
            "Keyword=Mutagen.Bethesda.Plugins.FormLinkOrIndex`1[Mutagen.Bethesda.Skyrim.IKeywordGetter]");

        Assert.Equal(
            UiText.Format(
                "Analysis.Condition.CombatTarget",
                UiText.Get("Analysis.Condition.Keyword.Fallback"),
                UiText.Get("Analysis.Condition.Keyword.Has")),
            MusicConditionFormatter.Format(condition));
        Assert.DoesNotContain("Mutagen", MusicConditionFormatter.Format(condition));
    }

    [Fact]
    public void Format_UsesResolvedKeywordEditorIdAndDisplayName()
    {
        var condition = new MusicConditionSource(
            "GetCombatTargetHasKeyword",
            "EqualTo",
            1f,
            string.Empty,
            "GetCombatTargetHasKeywordConditionData",
            "Keyword=035D59:Skyrim.esm")
        {
            KeywordFormKey = "035D59:Skyrim.esm",
            KeywordEditorId = "ActorTypeDragon",
            KeywordDisplayName = "ドラゴン",
            KeywordDefinitionPluginName = "Skyrim.esm"
        };

        Assert.Equal(
            UiText.Format(
                "Analysis.Condition.CombatTarget",
                UiText.Format("Analysis.Condition.LabelWithTechnicalName", "ActorTypeDragon", "ドラゴン"),
                UiText.Get("Analysis.Condition.Keyword.Has")),
            MusicConditionFormatter.Format(condition));
        Assert.Contains("KeywordFormKey=035D59:Skyrim.esm", MusicConditionFormatter.FormatTechnical(condition));
        Assert.Contains("KeywordPlugin=Skyrim.esm", MusicConditionFormatter.FormatTechnical(condition));
    }

    [Fact]
    public void Format_UsesGeneratedJapaneseKeywordExplanationWhenRecordHasNoName()
    {
        var condition = new MusicConditionSource(
            "GetCombatTargetHasKeyword",
            "EqualTo",
            0f,
            string.Empty,
            "GetCombatTargetHasKeywordConditionData",
            "Keyword=035D59:Skyrim.esm")
        {
            KeywordFormKey = "035D59:Skyrim.esm",
            KeywordEditorId = "ActorTypeDragon",
            KeywordJapaneseExplanation = "ドラゴン",
            KeywordExplanationSource = "EditorIDの一般語から自動補足"
        };

        Assert.Equal(
            UiText.Format(
                "Analysis.Condition.CombatTarget",
                UiText.Format("Analysis.Condition.LabelWithTechnicalName", "ActorTypeDragon", "ドラゴン"),
                UiText.Get("Analysis.Condition.Keyword.DoesNotHave")),
            MusicConditionFormatter.Format(condition));
        Assert.Contains("KeywordNameSource=EditorIDの一般語から自動補足", MusicConditionFormatter.FormatTechnical(condition));
    }

    [Fact]
    public void Format_UsesJapaneseWeatherExplanationWhenRecordHasNoName()
    {
        var condition = new MusicConditionSource(
            "GetIsCurrentWeather",
            "EqualTo",
            1f,
            string.Empty,
            "GetIsCurrentWeatherConditionData",
            "Weather=001234:Skyrim.esm")
        {
            WeatherFormKey = "001234:Skyrim.esm",
            WeatherEditorId = "SovngardeClear"
        };

        Assert.Equal(
            UiText.Format(
                "Analysis.Condition.Weather",
                UiText.Format(
                    "Analysis.Condition.LabelWithTechnicalName",
                    "SovngardeClear",
                    MusicWeatherNameFormatter.InferJapaneseName("SovngardeClear")!),
                UiText.Get("Analysis.Condition.Weather.Matches")),
            MusicConditionFormatter.Format(condition));
    }

    [Theory]
    [InlineData("LessThanOrEqualTo", 5f, "BeforeOrAt")]
    [InlineData("GreaterThanOrEqualTo", 22f, "AfterOrAt")]
    [InlineData("LessThan", 18f, "Before")]
    [InlineData("EqualTo", 5.5f, "Equal")]
    public void Format_TimeConditionUsesJapaneseTimeRange(
        string compareOperator,
        float value,
        string expectedOperator)
    {
        var condition = new MusicConditionSource(
            "GetCurrentTime",
            compareOperator,
            value,
            string.Empty,
            "GetCurrentTimeConditionData",
            string.Empty);

        var clock = value == 22f
            ? UiText.Format("Analysis.Condition.Clock.Hour", UiText.Get("Analysis.Condition.Clock.Pm"), 10)
            : value == 18f
                ? UiText.Format("Analysis.Condition.Clock.Hour", UiText.Get("Analysis.Condition.Clock.Pm"), 6)
                : value == 5.5f
                    ? UiText.Format("Analysis.Condition.Clock.HourMinute", UiText.Get("Analysis.Condition.Clock.Am"), 5, 30)
                    : UiText.Format("Analysis.Condition.Clock.Hour", UiText.Get("Analysis.Condition.Clock.Am"), 5);
        var expectedValue = expectedOperator == "Equal"
            ? clock
            : UiText.Format($"Analysis.Condition.Time.{expectedOperator}", clock);
        Assert.Equal(
            UiText.Format("Analysis.Condition.TimeOfDay", expectedValue),
            MusicConditionFormatter.Format(condition));
    }

    [Fact]
    public void Format_ExplainsWeatherAndInteriorConditions()
    {
        var weather = new MusicConditionSource(
            "GetIsCurrentWeather",
            "EqualTo",
            1f,
            string.Empty,
            "GetIsCurrentWeatherConditionData",
            "Weather=TechnicalLink");
        var interior = new MusicConditionSource(
            "IsInInterior",
            "EqualTo",
            0f,
            string.Empty,
            "IsInInteriorConditionData",
            string.Empty);

        Assert.Equal(
            UiText.Format(
                "Analysis.Condition.Weather",
                UiText.Get("Analysis.Condition.Weather.Fallback"),
                UiText.Get("Analysis.Condition.Weather.Matches")),
            MusicConditionFormatter.Format(weather));
        Assert.Equal(
            UiText.Get("Analysis.Condition.Boolean.Exterior"),
            MusicConditionFormatter.Format(interior));
    }

    [Fact]
    public void FormatTrackConditions_CombinesTimePairIntoReadableRange()
    {
        var conditions = new[]
        {
            new MusicConditionSource(
                "GetCurrentTime",
                "GreaterThanOrEqualTo",
                22f,
                "OR",
                "GetCurrentTimeConditionData",
                string.Empty),
            new MusicConditionSource(
                "GetCurrentTime",
                "LessThanOrEqualTo",
                5f,
                "OR",
                "GetCurrentTimeConditionData",
                string.Empty)
        };

        var range = UiText.Format(
            "Analysis.Condition.TimeRange",
            UiText.Get("Analysis.Condition.TimeRange.Night"),
            "22:00",
            "5:00");
        Assert.Equal(
            UiText.Format("Analysis.Condition.Summary", UiText.Format("Analysis.Condition.TimeOfDay", range)),
            MusicConditionFormatter.FormatTrackConditions(conditions));
        Assert.Equal(range, MusicConditionFormatter.FormatTimeRange(22f, 5f));
    }

    [Fact]
    public void Format_UsesResolvedWeatherEditorIdAndDisplayName()
    {
        var condition = new MusicConditionSource(
            "GetIsCurrentWeather",
            "EqualTo",
            1f,
            string.Empty,
            "GetIsCurrentWeatherConditionData",
            "Weather=001234:Skyrim.esm")
        {
            WeatherFormKey = "001234:Skyrim.esm",
            WeatherEditorId = "SkyrimWeatherRain",
            WeatherDisplayName = "雨"
        };

        Assert.Equal(
            UiText.Format(
                "Analysis.Condition.Weather",
                UiText.Format("Analysis.Condition.LabelWithTechnicalName", "SkyrimWeatherRain", "雨"),
                UiText.Get("Analysis.Condition.Weather.Matches")),
            MusicConditionFormatter.Format(condition));
        Assert.Contains("Weather=001234:Skyrim.esm", MusicConditionFormatter.FormatTechnical(condition));
    }

    [Fact]
    public void Format_UsesEnglishConditionLabelsAndClockFormat()
    {
        try
        {
            UiText.SetLanguage(UiLanguage.English);
            var condition = new MusicConditionSource(
                "GetCurrentTime",
                "GreaterThanOrEqualTo",
                5f,
                string.Empty,
                "GetCurrentTimeConditionData",
                string.Empty);

            Assert.Equal("Time of day: at or after 5 AM", MusicConditionFormatter.Format(condition));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }
}
