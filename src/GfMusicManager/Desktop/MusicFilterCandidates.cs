using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Desktop;

public sealed record MusicFilterCandidate(
    string Key,
    string DisplayText,
    string DetailText)
{
    public static MusicFilterCandidate All(string category) =>
        new(
            string.Empty,
            UiText.Get("Filter.All"),
            UiText.Format("Filter.AllCategory", category));
}

public sealed class MusicFilterCandidates
{
    private MusicFilterCandidates(
        IReadOnlyList<MusicFilterCandidate> combat,
        IReadOnlyList<MusicFilterCandidate> timeOfDay,
        IReadOnlyList<MusicFilterCandidate> weather,
        IReadOnlyList<MusicFilterCandidate> otherCondition)
    {
        Combat = combat;
        TimeOfDay = timeOfDay;
        Weather = weather;
        OtherCondition = otherCondition;
    }

    public IReadOnlyList<MusicFilterCandidate> Combat { get; }

    public IReadOnlyList<MusicFilterCandidate> TimeOfDay { get; }

    public IReadOnlyList<MusicFilterCandidate> Weather { get; }

    public IReadOnlyList<MusicFilterCandidate> OtherCondition { get; }

    public static MusicFilterCandidates FromTracks(IEnumerable<TrackRow> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var rows = tracks.ToArray();

        var combat = BuildCombatCandidates(rows);
        var timeOfDay = rows
            .SelectMany(row => MusicFilterMatcher.GetTimeSelectionGroups(row.MusicConditions))
            .Select(conditions => new MusicFilterCandidate(
                MusicFilterMatcher.CreateTimeSelectionKey(conditions),
                FormatTimeCandidate(conditions),
                string.Join(" / ", conditions.Select(MusicConditionFormatter.FormatTechnical))))
            .DistinctBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(candidate => candidate.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var weather = BuildWeatherCandidates(rows);

        var otherCondition = rows
            .SelectMany(row => row.MusicConditions)
            .Where(condition =>
                !MusicFilterMatcher.IsCombatCondition(condition) &&
                !MusicFilterMatcher.IsTimeCondition(condition) &&
                !MusicFilterMatcher.IsWeatherCondition(condition))
            .Select(condition => new MusicFilterCandidate(
                MusicConditionFormatter.CreateKey(condition),
                MusicConditionFormatter.Format(condition),
                MusicConditionFormatter.FormatTechnical(condition)))
            .DistinctBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(candidate => candidate.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MusicFilterCandidates(
            PrependAll(UiText.Get("Filter.Combat"), combat),
            PrependAll(UiText.Get("Filter.TimeOfDay"), timeOfDay),
            PrependAll(UiText.Get("Filter.Weather"), weather),
            PrependAll(UiText.Get("Filter.OtherCondition"), otherCondition));
    }

    private static IReadOnlyList<MusicFilterCandidate> BuildCombatCandidates(
        IReadOnlyList<TrackRow> rows)
    {
        var recordCandidates = rows
            .SelectMany(row => row.AvailableKeywordRecords)
            .Where(record => !string.IsNullOrWhiteSpace(record.FormKey))
            .Select(record => new MusicFilterCandidate(
                MusicFilterMatcher.CreateCombatSelectionKey(record.FormKey),
                FormatKeywordRecord(record),
                UiText.Format("Filter.SourceEsp", record.Plugin.Name, record.FormKey)))
            .DistinctBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase);

        var conditionCandidates = rows
            .SelectMany(row => row.MusicConditions)
            .Where(condition =>
                MusicFilterMatcher.IsCombatCondition(condition) &&
                condition.ComparisonValue != 0 &&
                !string.IsNullOrWhiteSpace(condition.KeywordFormKey))
            .Select(condition => new MusicFilterCandidate(
                MusicFilterMatcher.CreateCombatSelectionKey(condition),
                FormatKeywordCondition(condition),
                UiText.Format("Filter.MusicCondition", MusicConditionFormatter.FormatTechnical(condition))));

        return recordCandidates
            .Concat(conditionCandidates)
            .DistinctBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(candidate => candidate.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<MusicFilterCandidate> BuildWeatherCandidates(
        IReadOnlyList<TrackRow> rows)
    {
        var recordCandidates = rows
            .SelectMany(row => row.AvailableWeatherRecords)
            .Where(record => !string.IsNullOrWhiteSpace(record.FormKey))
            .Select(record => new MusicFilterCandidate(
                MusicFilterMatcher.CreateWeatherSelectionKey(record.FormKey),
                FormatWeatherRecord(record),
                UiText.Format("Filter.SourceEsp", record.Plugin.Name, record.FormKey)))
            .DistinctBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase);

        var conditionCandidates = rows
            .SelectMany(row => row.MusicConditions)
            .Where(condition =>
                MusicFilterMatcher.IsWeatherCondition(condition) &&
                condition.ComparisonValue != 0)
            .Select(condition => new MusicFilterCandidate(
                MusicFilterMatcher.CreateWeatherSelectionKey(condition),
                FormatWeatherCondition(condition),
                UiText.Format("Filter.MusicCondition", MusicConditionFormatter.FormatTechnical(condition))));

        return recordCandidates
            .Concat(conditionCandidates)
            .DistinctBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
            .OrderBy(candidate => candidate.DisplayText, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<MusicFilterCandidate> PrependAll(
        string category,
        IReadOnlyList<MusicFilterCandidate> candidates) =>
        new[] { MusicFilterCandidate.All(category) }
            .Concat(candidates)
            .ToArray();

    private static string FormatKeywordRecord(PluginRecordSource record)
    {
        var editorId = Clean(record.EditorId) ?? record.FormKey;
        var japanese = MusicKeywordNameFormatter.InferJapaneseName(record.EditorId);
        return AppendJapanese(editorId, japanese);
    }

    private static string FormatKeywordCondition(MusicConditionSource condition)
    {
        var editorId = Clean(condition.KeywordEditorId) ??
                       condition.KeywordFormKey ??
                       UiText.Get("Filter.KeywordFallback");
        return AppendJapanese(editorId, condition.KeywordJapaneseExplanation);
    }

    private static string FormatWeatherRecord(PluginRecordSource record)
    {
        return MusicWeatherNameFormatter.FormatLabel(record.EditorId, record.DisplayName) ??
               record.FormKey;
    }

    private static string FormatWeatherCondition(MusicConditionSource condition)
    {
        if (condition.FunctionName.Equals("IsRaining", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Get("Filter.Raining");
        }

        if (condition.FunctionName.Equals("IsSnowing", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Get("Filter.Snowing");
        }

        var editorId = Clean(condition.WeatherEditorId) ??
                       condition.WeatherFormKey ??
                       UiText.Get("Filter.WeatherFallback");
        return AppendJapanese(editorId, condition.WeatherJapaneseExplanation);
    }

    private static string FormatTimeCandidate(
        IReadOnlyList<MusicConditionSource> conditions)
    {
        var start = conditions.FirstOrDefault(condition =>
            condition.CompareOperator.Equals("GreaterThanOrEqualTo", StringComparison.OrdinalIgnoreCase) ||
            condition.CompareOperator.Equals("GreaterThan", StringComparison.OrdinalIgnoreCase));
        var end = conditions.FirstOrDefault(condition =>
            condition.CompareOperator.Equals("LessThanOrEqualTo", StringComparison.OrdinalIgnoreCase) ||
            condition.CompareOperator.Equals("LessThan", StringComparison.OrdinalIgnoreCase));
        if (start is not null && end is not null)
        {
            return MusicConditionFormatter.FormatTimeRange(
                start.ComparisonValue,
                end.ComparisonValue);
        }

        var single = conditions.FirstOrDefault();
        return single is null
            ? UiText.Get("Filter.TimeFallback")
            : MusicConditionFormatter.FormatWithoutCategory(single);
    }

    private static string AppendJapanese(string original, string? japanese)
    {
        return string.IsNullOrWhiteSpace(japanese) ||
               original.Equals(japanese, StringComparison.OrdinalIgnoreCase)
            ? original
            : $"{original}（{japanese}）";
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith("$", StringComparison.Ordinal)
            ? null
            : value.Trim();
}
