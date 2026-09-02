using GfMusicManager.Core.Localization;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Analysis;

public enum MusicSettingScope
{
    MusicType,
    Cell,
    Location,
    Region,
    WorldSpace
}

public sealed record MusicConditionSource(
    string FunctionName,
    string CompareOperator,
    float ComparisonValue,
    string Flags,
    string DataType,
    string DataSummary)
{
    public string? KeywordFormKey { get; init; }

    public string? WeatherFormKey { get; init; }

    public string? KeywordEditorId { get; init; }

    public string? KeywordDisplayName { get; init; }

    public string? KeywordJapaneseExplanation { get; init; }

    public string? KeywordExplanationSource { get; init; }

    public string? KeywordDefinitionPluginName { get; init; }

    public string? WeatherEditorId { get; init; }

    public string? WeatherDisplayName { get; init; }

    public string? WeatherJapaneseExplanation { get; init; }

    public string? WeatherExplanationSource { get; init; }

    public string? WeatherDefinitionPluginName { get; init; }

    public string ComparisonValueType { get; init; } = "Float";

    public string? ComparisonGlobalFormKey { get; init; }

    public string RunOnType { get; init; } = "Subject";

    public int RunOnTypeIndex { get; init; } = -1;

    public string? ReferenceFormKey { get; init; }

    public bool UseAliases { get; init; }

    public bool UsePackageData { get; init; }

    public int? FirstUnusedIntParameter { get; init; }

    public int? SecondUnusedIntParameter { get; init; }

    public string? FirstUnusedStringParameter { get; init; }

    public string? SecondUnusedStringParameter { get; init; }

    public bool IsEditable => IsEditableFunction(FunctionName);

    public static MusicConditionSource From(
        PluginRecordConditionSource condition,
        IReadOnlyDictionary<string, PluginRecordSource>? recordsByFormKey = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var source = new MusicConditionSource(
            condition.FunctionName,
            condition.CompareOperator,
            condition.ComparisonValue,
            condition.Flags,
            condition.DataType,
            condition.DataSummary)
        {
            KeywordFormKey = condition.KeywordFormKey,
            WeatherFormKey = condition.WeatherFormKey,
            ComparisonValueType = condition.ComparisonValueType,
            ComparisonGlobalFormKey = condition.ComparisonGlobalFormKey,
            RunOnType = condition.RunOnType,
            RunOnTypeIndex = condition.RunOnTypeIndex,
            ReferenceFormKey = condition.ReferenceFormKey,
            UseAliases = condition.UseAliases,
            UsePackageData = condition.UsePackageData,
            FirstUnusedIntParameter = condition.FirstUnusedIntParameter,
            SecondUnusedIntParameter = condition.SecondUnusedIntParameter,
            FirstUnusedStringParameter = condition.FirstUnusedStringParameter,
            SecondUnusedStringParameter = condition.SecondUnusedStringParameter
        };

        var keywordRecord = ResolveRecord(condition.KeywordFormKey, recordsByFormKey);
        var weatherRecord = ResolveRecord(condition.WeatherFormKey, recordsByFormKey);
        var japaneseExplanation = keywordRecord is null
            ? null
            : MusicKeywordNameFormatter.InferJapaneseName(keywordRecord.EditorId);
        var weatherJapaneseExplanation = weatherRecord is null
            ? null
            : MusicWeatherNameFormatter.InferJapaneseName(weatherRecord.EditorId);

        if (keywordRecord is null && weatherRecord is null)
        {
            return source;
        }

        return source with
        {
            KeywordEditorId = keywordRecord?.EditorId,
            KeywordDisplayName = keywordRecord?.DisplayName,
            KeywordJapaneseExplanation = japaneseExplanation,
            KeywordExplanationSource = keywordRecord is null ||
                                       !string.IsNullOrWhiteSpace(keywordRecord.DisplayName) ||
                                       japaneseExplanation is null
                ? null
                : UiText.Get("Analysis.Explanation.EditorIdFallback"),
            KeywordDefinitionPluginName = keywordRecord?.Plugin.Name,
            WeatherEditorId = weatherRecord?.EditorId,
            WeatherDisplayName = weatherRecord?.DisplayName,
            WeatherJapaneseExplanation = weatherJapaneseExplanation,
            WeatherExplanationSource = weatherRecord is null ||
                                        !string.IsNullOrWhiteSpace(weatherRecord.DisplayName) ||
                                        weatherJapaneseExplanation is null
                ? null
                : UiText.Get("Analysis.Explanation.EditorIdFallback"),
            WeatherDefinitionPluginName = weatherRecord?.Plugin.Name
        };
    }

    private static PluginRecordSource? ResolveRecord(
        string? formKey,
        IReadOnlyDictionary<string, PluginRecordSource>? recordsByFormKey)
    {
        return string.IsNullOrWhiteSpace(formKey) ||
               recordsByFormKey is null ||
               !recordsByFormKey.TryGetValue(formKey, out var record)
            ? null
            : record;
    }

    public static bool IsEditableFunction(string? functionName) =>
        functionName is not null &&
        (functionName.Equals("GetCurrentTime", StringComparison.OrdinalIgnoreCase) ||
         functionName.Equals("GetCombatTargetHasKeyword", StringComparison.OrdinalIgnoreCase) ||
         functionName.Equals("GetIsCurrentWeather", StringComparison.OrdinalIgnoreCase));

    public static MusicConditionSource CreateCombatKeyword(
        PluginRecordSource keyword,
        bool hasKeyword,
        string flags = "")
    {
        ArgumentNullException.ThrowIfNull(keyword);
        var japaneseExplanation = MusicKeywordNameFormatter.InferJapaneseName(keyword.EditorId);
        return new MusicConditionSource(
            "GetCombatTargetHasKeyword",
            "EqualTo",
            hasKeyword ? 1f : 0f,
            flags,
            "GetCombatTargetHasKeywordConditionData",
            $"Keyword={keyword.FormKey}")
        {
            KeywordFormKey = keyword.FormKey,
            KeywordEditorId = keyword.EditorId,
            KeywordDisplayName = keyword.DisplayName,
            KeywordJapaneseExplanation = japaneseExplanation,
            KeywordExplanationSource = string.IsNullOrWhiteSpace(keyword.DisplayName) &&
                                       japaneseExplanation is not null
                ? UiText.Get("Analysis.Explanation.EditorIdFallback")
                : null,
            KeywordDefinitionPluginName = keyword.Plugin.Name,
            RunOnType = "Reference",
            ReferenceFormKey = "000014:Skyrim.esm"
        };
    }

    public static MusicConditionSource CreateCurrentWeather(
        PluginRecordSource weather,
        bool matches,
        string flags = "")
    {
        ArgumentNullException.ThrowIfNull(weather);
        return new MusicConditionSource(
            "GetIsCurrentWeather",
            "EqualTo",
            matches ? 1f : 0f,
            flags,
            "GetIsCurrentWeatherConditionData",
            $"Weather={weather.FormKey}")
        {
            WeatherFormKey = weather.FormKey,
            WeatherEditorId = weather.EditorId,
            WeatherDisplayName = weather.DisplayName,
            WeatherJapaneseExplanation = MusicWeatherNameFormatter.InferJapaneseName(weather.EditorId),
            WeatherExplanationSource = string.IsNullOrWhiteSpace(weather.DisplayName) &&
                                       !string.IsNullOrWhiteSpace(MusicWeatherNameFormatter.InferJapaneseName(weather.EditorId))
                ? UiText.Get("Analysis.Explanation.EditorIdFallback")
                : null,
            WeatherDefinitionPluginName = weather.Plugin.Name
        };
    }

    public static MusicConditionSource CreateCurrentTime(
        float value,
        string compareOperator,
        string flags = "")
    {
        return new MusicConditionSource(
            "GetCurrentTime",
            compareOperator,
            value,
            flags,
            "GetCurrentTimeConditionData",
            string.Empty);
    }
}

public sealed record MusicTrackSource(
    string FormKey,
    string? EditorId,
    IReadOnlyList<string> AudioPaths,
    PluginRecordSource Record)
{
    public IReadOnlyList<string> ResolvedAudioPaths { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> MatchingAudioPaths => AudioPaths
        .Concat(ResolvedAudioPaths)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public bool MatchesAudioPath(string path) => MatchingAudioPaths.Any(audioPath =>
        string.Equals(
            MusicAudioPathIdentity.NormalizeMusicIdentity(audioPath),
            MusicAudioPathIdentity.NormalizeMusicIdentity(path),
            StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<MusicConditionSource> Conditions { get; init; } =
        Array.Empty<MusicConditionSource>();
}

public sealed record MusicSettingSource(
    MusicSettingScope Scope,
    string ScopeFormKey,
    string? ScopeEditorId,
    string MusicTypeFormKey,
    string? MusicTypeEditorId,
    PluginRecordSource Record,
    PluginRecordSource MusicTypeRecord,
    IReadOnlyList<MusicTrackSource> Tracks)
{
    public string ScopeLabel => Scope switch
    {
        MusicSettingScope.MusicType => UiText.Get("Scope.MusicType"),
        MusicSettingScope.Cell => UiText.Get("Scope.Cell"),
        MusicSettingScope.Location => UiText.Get("Scope.Location"),
        MusicSettingScope.Region => UiText.Get("Scope.Region"),
        MusicSettingScope.WorldSpace => UiText.Get("Scope.WorldSpace"),
        _ => Scope.ToString()
    };

    public string ScopeName => string.IsNullOrWhiteSpace(ScopeEditorId)
        ? ScopeFormKey
        : ScopeEditorId!;

    public string ScopeDisplayName => MusicScopeNameFormatter.Format(
        Scope,
        ScopeName,
        Record.DisplayName);

    public string ScopeLabelJapanese => MusicScopeNameFormatter.GetJapaneseLabel(Scope);

    public string MusicTypeDisplayName => MusicScopeNameFormatter.Format(
        MusicSettingScope.MusicType,
        MusicTypeEditorId ?? MusicTypeFormKey,
        MusicTypeRecord.DisplayName);

    public string MusicTypeDisplayNameWithoutSuffix =>
        MusicScopeNameFormatter.WithoutMusicTypeSuffix(MusicTypeDisplayName);

    public IReadOnlyList<string> AudioPaths => Tracks
        .SelectMany(track => track.MatchingAudioPaths)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public sealed record MusicDefinitionConflict(
    string FormKey,
    string RecordType,
    IReadOnlyList<PluginRecordSource> Definitions,
    PluginRecordSource CurrentWinner)
{
    public int DefinitionCount => Definitions.Count;

    public string DisplayName => Definitions
        .Select(record => record.DisplayName)
        .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ??
        Definitions
            .Select(record => record.EditorId)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ??
        FormKey;

    public string WinnerPluginName => CurrentWinner.Plugin.Name;

    public string SummaryText => UiText.Format(
        "Analysis.DefinitionConflict.Summary",
        DisplayName,
        DefinitionCount,
        WinnerPluginName);

    public string DetailText => string.Join(
        Environment.NewLine,
        Definitions.Select(record =>
            UiText.Format(
                "Analysis.DefinitionConflict.Detail",
                record.Plugin.Name,
                UiText.Get(record.IsWinner
                    ? "Analysis.DefinitionConflict.CurrentWinner"
                    : "Analysis.DefinitionConflict.Overridden"))));
}

public sealed record MusicAnalysisResult(
    IReadOnlyList<MusicSettingSource> Settings,
    IReadOnlyDictionary<string, IReadOnlyList<MusicSettingSource>> SettingsByAssetPath,
    IReadOnlyList<ScanIssue> Issues)
{
    public AdditionalMusicProjectRepairReport AdditionalMusicProjectRepair { get; init; } =
        AdditionalMusicProjectRepairReport.Empty;

    public FantasySoundtrackProjectRepairReport FantasySoundtrackProjectRepair { get; init; } =
        FantasySoundtrackProjectRepairReport.Empty;

    public IReadOnlyList<MusicConditionSource> ConditionCandidates { get; init; } =
        Array.Empty<MusicConditionSource>();

    public IReadOnlyList<PluginRecordSource> KeywordCandidates { get; init; } =
        Array.Empty<PluginRecordSource>();

    public IReadOnlyList<PluginRecordSource> WeatherCandidates { get; init; } =
        Array.Empty<PluginRecordSource>();

    public IReadOnlyList<MusicSettingSource> EffectiveSettings { get; init; } =
        Array.Empty<MusicSettingSource>();

    public IReadOnlyList<MusicDefinitionConflict> DefinitionConflicts { get; init; } =
        Array.Empty<MusicDefinitionConflict>();

    public IReadOnlyList<MusicSettingSource> GetSettingsForAsset(string virtualPath)
    {
        var key = MusicSettingsAnalyzer.NormalizeAssetPath(virtualPath);
        return SettingsByAssetPath.TryGetValue(key, out var settings)
            ? settings
            : Array.Empty<MusicSettingSource>();
    }

    public IReadOnlyList<MusicDefinitionConflict> GetDefinitionConflictsForAsset(string virtualPath)
    {
        var settings = GetSettingsForAsset(virtualPath);
        if (settings.Count == 0 || DefinitionConflicts.Count == 0)
        {
            return Array.Empty<MusicDefinitionConflict>();
        }

        var musicTypeKeys = settings
            .Select(setting => setting.MusicTypeFormKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settingRecordKeys = settings
            .Select(setting => setting.Record.FormKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trackKeys = settings
            .SelectMany(setting => setting.Tracks)
            .Select(track => track.FormKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return DefinitionConflicts
            .Where(conflict =>
                conflict.RecordType.Equals("MusicType", StringComparison.OrdinalIgnoreCase)
                    ? musicTypeKeys.Contains(conflict.FormKey)
                    : settingRecordKeys.Contains(conflict.FormKey) || trackKeys.Contains(conflict.FormKey))
            .ToArray();
    }
}
