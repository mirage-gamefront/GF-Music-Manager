using System.Globalization;
using GfMusicManager.Core.Localization;

namespace GfMusicManager.Core.Analysis;

public static class MusicConditionFormatter
{
    private static readonly IReadOnlyDictionary<string, string> FunctionNameKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["GetCurrentTime"] = "Analysis.Condition.Function.GetCurrentTime",
            ["GetInCell"] = "Analysis.Condition.Function.GetInCell",
            ["GetInWorldspace"] = "Analysis.Condition.Function.GetInWorldspace",
            ["GetInCurrentLoc"] = "Analysis.Condition.Function.GetInCurrentLoc",
            ["IsInInterior"] = "Analysis.Condition.Function.IsInInterior",
            ["IsRaining"] = "Analysis.Condition.Function.IsRaining",
            ["IsSnowing"] = "Analysis.Condition.Function.IsSnowing",
            ["GetRandomPercent"] = "Analysis.Condition.Function.GetRandomPercent",
            ["GetDayOfWeek"] = "Analysis.Condition.Function.GetDayOfWeek",
            ["GetCombatTargetHasKeyword"] = "Analysis.Condition.Function.GetCombatTargetHasKeyword",
            ["GetIsCurrentWeather"] = "Analysis.Condition.Function.GetIsCurrentWeather"
        };

    private static readonly IReadOnlyDictionary<string, string> Operators =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EqualTo"] = "=",
            ["NotEqualTo"] = "≠",
            ["LessThan"] = "<",
            ["LessThanOrEqualTo"] = "≤",
            ["GreaterThan"] = ">",
            ["GreaterThanOrEqualTo"] = "≥"
        };

    public static string Format(MusicConditionSource condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var text = condition.FunctionName switch
        {
            "GetCurrentTime" => FormatTime(condition),
            "GetCombatTargetHasKeyword" => FormatCombatKeyword(condition),
            "GetIsCurrentWeather" => FormatCurrentWeather(condition),
            "IsRaining" => FormatBoolean(
                condition,
                "Analysis.Condition.Boolean.Raining",
                "Analysis.Condition.Boolean.NotRaining"),
            "IsSnowing" => FormatBoolean(
                condition,
                "Analysis.Condition.Boolean.Snowing",
                "Analysis.Condition.Boolean.NotSnowing"),
            "IsInInterior" => FormatBoolean(
                condition,
                "Analysis.Condition.Boolean.Interior",
                "Analysis.Condition.Boolean.Exterior"),
            "GetInCell" => FormatBoolean(
                condition,
                "Analysis.Condition.Boolean.InCell",
                "Analysis.Condition.Boolean.OutOfCell"),
            "GetInWorldspace" => FormatBoolean(
                condition,
                "Analysis.Condition.Boolean.InWorldSpace",
                "Analysis.Condition.Boolean.OutOfWorldSpace"),
            "GetInCurrentLoc" => FormatBoolean(
                condition,
                "Analysis.Condition.Boolean.InLocation",
                "Analysis.Condition.Boolean.OutOfLocation"),
            "GetRandomPercent" => FormatNumeric(
                condition,
                "Analysis.Condition.Numeric.RandomPercent"),
            "GetDayOfWeek" => FormatNumeric(
                condition,
                "Analysis.Condition.Numeric.DayOfWeek"),
            _ => FormatUnknown(condition)
        };

        return AppendConditionGroup(text, condition);
    }

    /// <summary>
    /// Formats the conditions as they should be shown to a user.  A pair of
    /// current-time conditions is presented as one readable time range while
    /// unsupported or single conditions retain their individual wording.
    /// </summary>
    public static string FormatTrackConditions(IEnumerable<MusicConditionSource> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        var list = conditions.ToArray();
        if (list.Length == 0)
        {
            return UiText.Get("Analysis.Condition.None");
        }

        var timeConditions = list
            .Where(condition => condition.FunctionName.Equals("GetCurrentTime", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var formatted = new List<string>();
        var consumedTimeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TryFormatTimeRange(timeConditions, out var timeText, out var consumedTimeConditions))
        {
            formatted.Add(timeText);
            foreach (var condition in consumedTimeConditions)
            {
                consumedTimeKeys.Add(CreateKey(condition));
            }
        }

        formatted.AddRange(list
            .Where(condition => !consumedTimeKeys.Contains(CreateKey(condition)))
            .Select(Format));
        return UiText.Format(
            "Analysis.Condition.Summary",
            string.Join(UiText.Get("Analysis.Condition.ListSeparator"), formatted));
    }

    public static string FormatTimeRange(float start, float end)
    {
        var rangeName = GetTimeRangeName(start, end);
        return UiText.Format(
            "Analysis.Condition.TimeRange",
            rangeName,
            FormatClock24(start),
            FormatClock24(end));
    }

    public static string FormatWithoutCategory(MusicConditionSource condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return condition.FunctionName.Equals("GetCurrentTime", StringComparison.OrdinalIgnoreCase)
            ? AppendConditionGroup(FormatTimeValue(condition), condition)
            : Format(condition);
    }

    private static string FormatTime(MusicConditionSource condition)
    {
        return UiText.Format("Analysis.Condition.TimeOfDay", FormatTimeValue(condition));
    }

    private static string FormatTimeValue(MusicConditionSource condition)
    {
        var time = FormatClock(condition.ComparisonValue);
        return condition.CompareOperator switch
        {
            "LessThanOrEqualTo" => UiText.Format("Analysis.Condition.Time.BeforeOrAt", time),
            "LessThan" => UiText.Format("Analysis.Condition.Time.Before", time),
            "GreaterThanOrEqualTo" => UiText.Format("Analysis.Condition.Time.AfterOrAt", time),
            "GreaterThan" => UiText.Format("Analysis.Condition.Time.After", time),
            "EqualTo" => time,
            "NotEqualTo" => UiText.Format("Analysis.Condition.Time.OtherThan", time),
            _ => UiText.Format(
                "Analysis.Condition.Time.WithOperator",
                time,
                FormatOperator(condition.CompareOperator))
        };
    }

    private static string FormatCombatKeyword(MusicConditionSource condition)
    {
        var hasKeyword = condition.ComparisonValue != 0;
        var keyword = FormatKeywordLabel(condition);
        var result = UiText.Get(
            hasKeyword
                ? "Analysis.Condition.Keyword.Has"
                : "Analysis.Condition.Keyword.DoesNotHave");
        return UiText.Format("Analysis.Condition.CombatTarget", keyword, result);
    }

    private static string FormatKeywordLabel(MusicConditionSource condition)
    {
        var editorId = CleanLabel(condition.KeywordEditorId);
        var displayName = UiText.Language.Equals(UiLanguage.Japanese, StringComparison.OrdinalIgnoreCase)
            ? CleanLabel(condition.KeywordJapaneseExplanation)
            : null;
        displayName ??= CleanLabel(condition.KeywordDisplayName);
        if (!string.IsNullOrWhiteSpace(editorId) &&
            !string.IsNullOrWhiteSpace(displayName) &&
            !editorId.Equals(displayName, StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Format(
                "Analysis.Condition.LabelWithTechnicalName",
                editorId,
                displayName);
        }

        return editorId ?? displayName ?? UiText.Get("Analysis.Condition.Keyword.Fallback");
    }

    private static string? CleanLabel(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith("$", StringComparison.Ordinal)
            ? null
            : value.Trim();

    private static string FormatCurrentWeather(MusicConditionSource condition)
    {
        var matches = condition.ComparisonValue != 0;
        var weather = FormatWeatherLabel(condition);
        var result = UiText.Get(
            matches
                ? "Analysis.Condition.Weather.Matches"
                : "Analysis.Condition.Weather.DoesNotMatch");
        return UiText.Format("Analysis.Condition.Weather", weather, result);
    }

    private static string FormatWeatherLabel(MusicConditionSource condition)
    {
        var editorId = CleanLabel(condition.WeatherEditorId);
        var displayName = UiText.Language.Equals(UiLanguage.Japanese, StringComparison.OrdinalIgnoreCase)
            ? CleanLabel(condition.WeatherJapaneseExplanation) ??
              CleanLabel(MusicWeatherNameFormatter.InferJapaneseName(editorId))
            : null;
        displayName ??= CleanLabel(condition.WeatherDisplayName);
        if (!string.IsNullOrWhiteSpace(editorId) &&
            !string.IsNullOrWhiteSpace(displayName) &&
            !editorId.Equals(displayName, StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Format(
                "Analysis.Condition.LabelWithTechnicalName",
                editorId,
                displayName);
        }

        return editorId ?? displayName ?? UiText.Get("Analysis.Condition.Weather.Fallback");
    }

    private static string FormatBoolean(
        MusicConditionSource condition,
        string trueKey,
        string falseKey)
    {
        var isTrue = condition.ComparisonValue != 0;
        if (condition.CompareOperator.Equals("NotEqualTo", StringComparison.OrdinalIgnoreCase))
        {
            isTrue = !isTrue;
        }

        return UiText.Get(isTrue ? trueKey : falseKey);
    }

    private static string FormatNumeric(MusicConditionSource condition, string labelKey)
    {
        var value = condition.ComparisonValue.ToString("0.###", CultureInfo.InvariantCulture);
        return UiText.Format(
            "Analysis.Condition.Numeric",
            UiText.Get(labelKey),
            FormatOperator(condition.CompareOperator),
            value);
    }

    private static string FormatUnknown(MusicConditionSource condition)
    {
        var label = FunctionNameKeys.TryGetValue(condition.FunctionName, out var key)
            ? UiText.Get(key)
            : UiText.Get("Analysis.Condition.Function.Other");
        return UiText.Format(
            "Analysis.Condition.Numeric",
            label,
            FormatOperator(condition.CompareOperator),
            condition.ComparisonValue.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static string AppendConditionGroup(string text, MusicConditionSource condition) =>
        condition.Flags.Contains("OR", StringComparison.OrdinalIgnoreCase)
            ? text + UiText.Get("Analysis.Condition.AnyOfGroup")
            : text;

    private static string FormatClock(float value)
    {
        var normalized = ((value % 24f) + 24f) % 24f;
        var totalMinutes = (int)MathF.Round(normalized * 60f, MidpointRounding.AwayFromZero) % (24 * 60);
        var hour24 = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        if (hour24 == 12 && minutes == 0)
        {
            return UiText.Get("Analysis.Condition.Clock.Noon");
        }

        var period = UiText.Get(
            hour24 < 12
                ? "Analysis.Condition.Clock.Am"
                : "Analysis.Condition.Clock.Pm");
        var hour = hour24 <= 12 ? hour24 : hour24 - 12;
        return minutes == 0
            ? UiText.Format("Analysis.Condition.Clock.Hour", period, hour)
            : UiText.Format("Analysis.Condition.Clock.HourMinute", period, hour, minutes);
    }

    private static string FormatClock24(float value)
    {
        var normalized = ((value % 24f) + 24f) % 24f;
        var totalMinutes = (int)MathF.Round(normalized * 60f, MidpointRounding.AwayFromZero) % (24 * 60);
        return $"{totalMinutes / 60}:{totalMinutes % 60:00}";
    }

    private static bool TryFormatTimeRange(
        IReadOnlyList<MusicConditionSource> conditions,
        out string text,
        out IReadOnlyList<MusicConditionSource> consumed)
    {
        var starts = conditions
            .Where(condition => condition.CompareOperator.Equals("GreaterThanOrEqualTo", StringComparison.OrdinalIgnoreCase) ||
                                condition.CompareOperator.Equals("GreaterThan", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var ends = conditions
            .Where(condition => condition.CompareOperator.Equals("LessThanOrEqualTo", StringComparison.OrdinalIgnoreCase) ||
                                condition.CompareOperator.Equals("LessThan", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var start in starts)
        {
            var end = ends.FirstOrDefault(candidate =>
                !CreateKey(candidate).Equals(CreateKey(start), StringComparison.OrdinalIgnoreCase));
            if (end is null)
            {
                continue;
            }

            text = UiText.Format(
                "Analysis.Condition.TimeOfDay",
                FormatTimeRange(start.ComparisonValue, end.ComparisonValue));
            consumed = new[] { start, end };
            return true;
        }

        text = string.Empty;
        consumed = Array.Empty<MusicConditionSource>();
        return false;
    }

    private static string GetTimeRangeName(float start, float end)
    {
        var normalizedStart = NormalizeHour(start);
        var normalizedEnd = NormalizeHour(end);
        if (NearlyEquals(normalizedStart, 5f) && NearlyEquals(normalizedEnd, 8f))
        {
            return UiText.Get("Analysis.Condition.TimeRange.Morning");
        }

        if (NearlyEquals(normalizedStart, 8f) && NearlyEquals(normalizedEnd, 18f))
        {
            return UiText.Get("Analysis.Condition.TimeRange.Day");
        }

        if (NearlyEquals(normalizedStart, 18f) && NearlyEquals(normalizedEnd, 22f))
        {
            return UiText.Get("Analysis.Condition.TimeRange.Evening");
        }

        if (NearlyEquals(normalizedStart, 22f) && NearlyEquals(normalizedEnd, 5f))
        {
            return UiText.Get("Analysis.Condition.TimeRange.Night");
        }

        if (NearlyEquals(normalizedStart, 5f) && NearlyEquals(normalizedEnd, 22f))
        {
            return UiText.Get("Analysis.Condition.TimeRange.Daytime");
        }

        return UiText.Get("Analysis.Condition.TimeRange.Custom");
    }

    private static float NormalizeHour(float value) => ((value % 24f) + 24f) % 24f;

    private static bool NearlyEquals(float left, float right) => MathF.Abs(left - right) < 0.01f;

    private static string FormatOperator(string compareOperator) =>
        Operators.TryGetValue(compareOperator, out var symbol)
            ? symbol
            : compareOperator;

    public static string FormatTechnical(MusicConditionSource condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var keywordDetails = string.IsNullOrWhiteSpace(condition.KeywordFormKey)
            ? string.Empty
            : $" / Keyword={FormatKeywordLabel(condition)}" +
              $" / KeywordFormKey={condition.KeywordFormKey}" +
              (string.IsNullOrWhiteSpace(condition.KeywordExplanationSource)
                  ? string.Empty
                  : $" / KeywordNameSource={condition.KeywordExplanationSource}") +
              (string.IsNullOrWhiteSpace(condition.KeywordDefinitionPluginName)
                  ? string.Empty
                  : $" / KeywordPlugin={condition.KeywordDefinitionPluginName}");
        var weatherDetails = string.IsNullOrWhiteSpace(condition.WeatherFormKey)
            ? string.Empty
            : $" / Weather={condition.WeatherFormKey}" +
              (string.IsNullOrWhiteSpace(condition.WeatherEditorId)
                  ? string.Empty
                  : $" / WeatherEditorID={condition.WeatherEditorId}") +
              (string.IsNullOrWhiteSpace(condition.WeatherDefinitionPluginName)
                  ? string.Empty
                  : $" / WeatherPlugin={condition.WeatherDefinitionPluginName}");
        var executionDetails =
            $" / ValueType={condition.ComparisonValueType}" +
            $" / RunOn={condition.RunOnType}" +
            $" / RunOnIndex={condition.RunOnTypeIndex}" +
            (string.IsNullOrWhiteSpace(condition.ReferenceFormKey)
                ? string.Empty
                : $" / Reference={condition.ReferenceFormKey}") +
            (condition.UseAliases ? " / UseAliases=True" : string.Empty) +
            (condition.UsePackageData ? " / UsePackageData=True" : string.Empty);

        return $"{condition.FunctionName} {condition.CompareOperator} " +
               $"{condition.ComparisonValue.ToString(CultureInfo.InvariantCulture)}" +
               $" / Flags={condition.Flags} / Data={condition.DataType}" +
               executionDetails +
               (string.IsNullOrWhiteSpace(condition.DataSummary)
                   ? string.Empty
                   : $" / {condition.DataSummary}") +
               keywordDetails +
               weatherDetails;
    }

    public static string CreateKey(MusicConditionSource condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return string.Join(
            "\u001f",
            CreateRecordKey(condition),
            condition.DataSummary);
    }

    public static string CreateRecordKey(MusicConditionSource condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return string.Join(
            "\u001f",
            condition.FunctionName,
            condition.CompareOperator,
            condition.ComparisonValue.ToString("R", CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(condition.Flags) ? "0" : condition.Flags,
            condition.DataType,
            condition.KeywordFormKey ?? string.Empty,
            condition.WeatherFormKey ?? string.Empty,
            string.IsNullOrWhiteSpace(condition.ComparisonValueType)
                ? "Float"
                : condition.ComparisonValueType,
            condition.ComparisonGlobalFormKey ?? string.Empty,
            string.IsNullOrWhiteSpace(condition.RunOnType) ? "Subject" : condition.RunOnType,
            condition.RunOnTypeIndex.ToString(CultureInfo.InvariantCulture),
            condition.ReferenceFormKey ?? string.Empty,
            condition.UseAliases,
            condition.UsePackageData,
            (condition.FirstUnusedIntParameter ?? 0).ToString(CultureInfo.InvariantCulture),
            (condition.SecondUnusedIntParameter ?? 0).ToString(CultureInfo.InvariantCulture),
            condition.FirstUnusedStringParameter ?? string.Empty,
            condition.SecondUnusedStringParameter ?? string.Empty);
    }
}
