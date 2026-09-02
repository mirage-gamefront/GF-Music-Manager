using GfMusicManager.Core.Analysis;

namespace GfMusicManager.Desktop;

public enum MusicPlaybackFilterKind
{
    Combat,
    TimeOfDay,
    Weather,
    OtherCondition,
    NoCondition
}

public sealed class MusicFilterOptions
{
    public static MusicFilterOptions Empty { get; } = new();

    public MusicFilterOptions(
        IEnumerable<KeyValuePair<MusicPlaybackFilterKind, string>>? playbackSelections = null,
        IEnumerable<KeyValuePair<MusicSettingScope, string>>? definitionSelections = null)
    {
        PlaybackSelections = (playbackSelections ?? Array.Empty<KeyValuePair<MusicPlaybackFilterKind, string>>())
            .GroupBy(pair => pair.Key)
            .ToDictionary(
                group => group.Key,
                group => group.First().Value?.Trim() ?? string.Empty);
        DefinitionSelections = (definitionSelections ?? Array.Empty<KeyValuePair<MusicSettingScope, string>>())
            .GroupBy(pair => pair.Key)
            .ToDictionary(
                group => group.Key,
                group => group.First().Value?.Trim() ?? string.Empty);
    }

    public MusicFilterOptions(
        IEnumerable<MusicPlaybackFilterKind> playbackFilters,
        IEnumerable<KeyValuePair<MusicSettingScope, string>>? definitionSelections = null)
        : this(
            (playbackFilters ?? Array.Empty<MusicPlaybackFilterKind>())
                .Select(filter => new KeyValuePair<MusicPlaybackFilterKind, string>(filter, string.Empty)),
            definitionSelections)
    {
    }

    public IReadOnlyDictionary<MusicPlaybackFilterKind, string> PlaybackSelections { get; }

    public IReadOnlySet<MusicPlaybackFilterKind> PlaybackFilters => PlaybackSelections.Keys.ToHashSet();

    public IReadOnlyDictionary<MusicSettingScope, string> DefinitionSelections { get; }

    public IReadOnlyDictionary<MusicSettingScope, string> DefinitionQueries => DefinitionSelections
        .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
        .ToDictionary(pair => pair.Key, pair => pair.Value);

    public bool IsEmpty => PlaybackSelections.Count == 0 && DefinitionSelections.Count == 0;

    public int ActiveRuleCount => PlaybackSelections.Count + DefinitionSelections.Count;
}

public static class MusicFilterMatcher
{
    public static bool Matches(TrackRow row, MusicFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(options);

        if (options.PlaybackSelections.Count > 0 &&
            !options.PlaybackSelections.Any(selection =>
                MatchesPlaybackFilter(row, selection.Key, selection.Value)))
        {
            return false;
        }

        if (options.DefinitionSelections.Count > 0 &&
            !options.DefinitionSelections.Any(selection =>
                MatchesDefinitionQuery(row, selection.Key, selection.Value)))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesPlaybackFilter(
        TrackRow row,
        MusicPlaybackFilterKind filter,
        string selectionKey) =>
        filter switch
        {
            MusicPlaybackFilterKind.Combat => string.IsNullOrWhiteSpace(selectionKey)
                ? HasCombatMusicType(row) || row.MusicConditions.Any(IsCombatCondition)
                : row.MusicConditions.Any(condition =>
                    IsCombatCondition(condition) &&
                    condition.ComparisonValue != 0 &&
                    CreateCombatSelectionKey(condition).Equals(selectionKey, StringComparison.OrdinalIgnoreCase)),
            MusicPlaybackFilterKind.TimeOfDay => string.IsNullOrWhiteSpace(selectionKey)
                ? row.MusicConditions.Any(IsTimeCondition)
                : GetTimeSelectionGroups(row.MusicConditions)
                    .Any(group => CreateTimeSelectionKey(group)
                        .Equals(selectionKey, StringComparison.OrdinalIgnoreCase)),
            MusicPlaybackFilterKind.Weather => string.IsNullOrWhiteSpace(selectionKey)
                ? row.MusicConditions.Any(IsWeatherCondition)
                : row.MusicConditions.Any(condition =>
                    IsWeatherCondition(condition) &&
                    condition.ComparisonValue != 0 &&
                    CreateWeatherSelectionKey(condition).Equals(selectionKey, StringComparison.OrdinalIgnoreCase)),
            MusicPlaybackFilterKind.OtherCondition => string.IsNullOrWhiteSpace(selectionKey)
                ? row.MusicConditions.Any(condition =>
                    !IsCombatCondition(condition) &&
                    !IsTimeCondition(condition) &&
                    !IsWeatherCondition(condition))
                : row.MusicConditions.Any(condition =>
                    !IsCombatCondition(condition) &&
                    !IsTimeCondition(condition) &&
                    !IsWeatherCondition(condition) &&
                    MusicConditionFormatter.CreateKey(condition)
                        .Equals(selectionKey, StringComparison.OrdinalIgnoreCase)),
            MusicPlaybackFilterKind.NoCondition => row.MusicConditions.Count == 0,
            _ => false
        };

    private static bool HasCombatMusicType(TrackRow row) => row.MusicSettings.Any(setting =>
        ContainsIgnoreCase(setting.MusicTypeEditorId, "Combat") ||
        ContainsIgnoreCase(setting.MusicTypeRecord.EditorId, "Combat"));

    private static bool MatchesDefinitionQuery(
        TrackRow row,
        MusicSettingScope scope,
        string query) => row.MusicSettings
            .Where(setting => setting.Scope == scope)
            .Any(setting =>
                string.IsNullOrWhiteSpace(query) ||
                ContainsIgnoreCase(setting.ScopeName, query) ||
                ContainsIgnoreCase(setting.ScopeFormKey, query) ||
                ContainsIgnoreCase(setting.ScopeDisplayName, query) ||
                ContainsIgnoreCase(setting.Record.EditorId, query) ||
                ContainsIgnoreCase(setting.Record.FormKey, query) ||
                ContainsIgnoreCase(setting.Record.DisplayName, query));

    internal static bool IsCombatCondition(MusicConditionSource condition) =>
        condition.FunctionName.Equals("GetCombatTargetHasKeyword", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTimeCondition(MusicConditionSource condition) =>
        condition.FunctionName.Equals("GetCurrentTime", StringComparison.OrdinalIgnoreCase);

    internal static bool IsWeatherCondition(MusicConditionSource condition) =>
        condition.FunctionName.Equals("GetIsCurrentWeather", StringComparison.OrdinalIgnoreCase) ||
        condition.FunctionName.Equals("IsRaining", StringComparison.OrdinalIgnoreCase) ||
        condition.FunctionName.Equals("IsSnowing", StringComparison.OrdinalIgnoreCase);

    public static string CreateCombatSelectionKey(MusicConditionSource condition) =>
        CreateCombatSelectionKey(condition.KeywordFormKey);

    public static string CreateCombatSelectionKey(string? keywordFormKey) =>
        $"combat:{keywordFormKey ?? string.Empty}:has";

    public static string CreateWeatherSelectionKey(MusicConditionSource condition) =>
        string.IsNullOrWhiteSpace(condition.WeatherFormKey)
            ? $"weather:{MusicConditionFormatter.CreateKey(condition)}"
            : CreateWeatherSelectionKey(condition.WeatherFormKey);

    public static string CreateWeatherSelectionKey(string? weatherFormKey) =>
        string.IsNullOrWhiteSpace(weatherFormKey)
            ? "weather:"
            : $"weather:{weatherFormKey}:matches";

    public static string CreateTimeSelectionKey(IEnumerable<MusicConditionSource> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        return string.Join(
            "|",
            conditions
                .Where(IsTimeCondition)
                .Select(MusicConditionFormatter.CreateKey)
                .Order(StringComparer.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<IReadOnlyList<MusicConditionSource>> GetTimeSelectionGroups(
        IEnumerable<MusicConditionSource> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        var timeConditions = conditions
            .Where(IsTimeCondition)
            .ToArray();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<IReadOnlyList<MusicConditionSource>>();
        var starts = timeConditions
            .Where(condition =>
                condition.CompareOperator.Equals("GreaterThanOrEqualTo", StringComparison.OrdinalIgnoreCase) ||
                condition.CompareOperator.Equals("GreaterThan", StringComparison.OrdinalIgnoreCase));
        var ends = timeConditions
            .Where(condition =>
                condition.CompareOperator.Equals("LessThanOrEqualTo", StringComparison.OrdinalIgnoreCase) ||
                condition.CompareOperator.Equals("LessThan", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var start in starts)
        {
            var startKey = MusicConditionFormatter.CreateKey(start);
            var end = ends.FirstOrDefault(candidate =>
            {
                var endKey = MusicConditionFormatter.CreateKey(candidate);
                return !startKey.Equals(endKey, StringComparison.OrdinalIgnoreCase) &&
                       !consumed.Contains(endKey);
            });
            if (end is null)
            {
                continue;
            }

            groups.Add(new[] { start, end });
            consumed.Add(startKey);
            consumed.Add(MusicConditionFormatter.CreateKey(end));
        }

        foreach (var condition in timeConditions)
        {
            if (!consumed.Contains(MusicConditionFormatter.CreateKey(condition)))
            {
                groups.Add(new[] { condition });
            }
        }

        return groups;
    }

    private static bool ContainsIgnoreCase(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(query, StringComparison.OrdinalIgnoreCase);
}
