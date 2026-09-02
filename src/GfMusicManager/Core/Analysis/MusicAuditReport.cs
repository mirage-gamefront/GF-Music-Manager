using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Analysis;

public sealed record MusicAuditAssetRow(
    int Index,
    string ModName,
    bool ModEnabled,
    string SourceKind,
    string VirtualPath,
    string SourcePath,
    string? ArchiveEntryPath,
    long? Length,
    string MappingStatus,
    int SettingCount,
    IReadOnlyList<string> SettingScopes,
    IReadOnlyList<string> SettingNames,
    string Placement,
    IReadOnlyList<string> Flags);

public sealed record MusicAuditIssue(
    string Code,
    string Severity,
    string Subject,
    string Message,
    string? Detail = null);

public sealed record MusicAuditConditionRow(
    string FunctionName,
    string CompareOperator,
    float ComparisonValue,
    string Flags,
    string DataType,
    string DataSummary)
{
    public string? KeywordFormKey { get; init; }

    public string? KeywordEditorId { get; init; }

    public string? KeywordDisplayName { get; init; }

    public string? KeywordJapaneseExplanation { get; init; }

    public string? KeywordExplanationSource { get; init; }

    public string? KeywordDefinitionPluginName { get; init; }

    public string? WeatherFormKey { get; init; }

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
}

public sealed record MusicAuditRecordRow(
    string FormKey,
    string RecordType,
    string? EditorId,
    string? DisplayName,
    bool IsDeleted,
    bool IsWinner,
    string PluginName,
    string ModName,
    bool PluginEnabled,
    IReadOnlyList<string> References,
    IReadOnlyList<string> Assets,
    IReadOnlyList<MusicAuditConditionRow> Conditions);

public sealed record MusicAuditReport(
    string ReportVersion,
    DateTimeOffset GeneratedAtUtc,
    string Mo2Root,
    string ProfileName,
    bool IncludeDisabledMods,
    int ModCount,
    int PluginCount,
    int RecordCount,
    int MusicSettingCount,
    int AssetCount,
    int MappedAssetCount,
    int UnmappedAssetCount,
    int ScanIssueCount,
    int MusicAnalysisIssueCount,
    int DuplicateVirtualPathGroupCount,
    int DuplicateVirtualPathRowCount,
    int LongPlacementCount,
    int RepeatedPlacementLabelCount,
    IReadOnlyDictionary<string, int> AssetsByMod,
    IReadOnlyDictionary<string, int> AssetsBySourceKind,
    IReadOnlyDictionary<string, int> MappedAssetsByMod,
    IReadOnlyDictionary<string, int> SettingsByScope,
    IReadOnlyList<MusicAuditAssetRow> Assets,
    IReadOnlyList<MusicAuditRecordRow> Records,
    IReadOnlyList<MusicAuditRecordRow> ContextRecords,
    IReadOnlyList<MusicAuditIssue> Issues);

public sealed class MusicAuditReportBuilder
{
    public const int DefaultLongPlacementThreshold = 160;

    public MusicAuditReport Build(
        ScanResult scan,
        MusicAnalysisResult analysis,
        int longPlacementThreshold = DefaultLongPlacementThreshold,
        bool includeDisabledMods = false)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(analysis);
        if (longPlacementThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(longPlacementThreshold));
        }

        var duplicateGroups = scan.Assets
            .GroupBy(asset => asset.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToArray();
        var duplicatePaths = duplicateGroups
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = scan.Assets
            .OrderBy(asset => asset.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(asset => asset.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .Select((asset, index) => BuildAssetRow(
                asset,
                index + 1,
                analysis.GetSettingsForAsset(asset.VirtualPath),
                duplicatePaths,
                longPlacementThreshold))
            .ToArray();

        var mappedRows = rows.Where(row => row.MappingStatus == "Mapped").ToArray();
        var musicSourceRecords = scan.Records
            .Where(IsMusicRecord)
            .ToArray();
        var recordsByFormKey = BuildRecordLookup(scan.Records);
        var recordRows = musicSourceRecords
            .OrderBy(record => record.Plugin.LoadOrderIndex)
            .ThenBy(record => record.FormKey, StringComparer.OrdinalIgnoreCase)
            .Select(record => BuildRecordRow(record, recordsByFormKey))
            .ToArray();
        var contextKeys = musicSourceRecords
            .SelectMany(record => record.References)
            .Select(reference => reference.FormKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contextRecordRows = scan.Records
            .Where(record =>
                contextKeys.Contains(record.FormKey) &&
                !IsMusicRecord(record) &&
                IsMusicContextRecord(record))
            .OrderBy(record => record.Plugin.LoadOrderIndex)
            .ThenBy(record => record.FormKey, StringComparer.OrdinalIgnoreCase)
            .Select(record => BuildRecordRow(record, recordsByFormKey))
            .ToArray();
        var auditIssues = new List<MusicAuditIssue>();
        AddScanIssues(auditIssues, scan.Issues, "ScanIssue");
        AddScanIssues(auditIssues, analysis.Issues, "MusicAnalysisIssue");

        if (rows.Any(row => row.MappingStatus == "Unmapped"))
        {
            var unmapped = rows
                .Where(row => row.MappingStatus == "Unmapped")
                .Select(row => $"{row.ModName}:{row.VirtualPath}")
                .ToArray();
            auditIssues.Add(new MusicAuditIssue(
                "UnmappedAsset",
                "Info",
                "Audio assets",
                $"{unmapped.Length} audio assets have no matching Music Type / Music Track setting in the scanned load order.",
                string.Join(Environment.NewLine, unmapped)));
        }

        foreach (var duplicateGroup in duplicateGroups)
        {
            auditIssues.Add(new MusicAuditIssue(
                "DuplicateVirtualPath",
                "Info",
                duplicateGroup.Key,
                "The same virtual audio path is present in more than one scanned source.",
                string.Join(Environment.NewLine, duplicateGroup.Select(asset => asset.ModName))));
        }

        foreach (var row in rows.Where(row => row.Flags.Contains("PlacementTooLong", StringComparer.Ordinal)))
        {
            auditIssues.Add(new MusicAuditIssue(
                "PlacementTooLong",
                "Warning",
                $"{row.ModName}:{row.VirtualPath}",
                $"The compact placement text is longer than {longPlacementThreshold} characters.",
                row.Placement));
        }

        foreach (var row in rows.Where(row => row.Flags.Contains("RepeatedPlacementLabel", StringComparer.Ordinal)))
        {
            auditIssues.Add(new MusicAuditIssue(
                "RepeatedPlacementLabel",
                "Error",
                $"{row.ModName}:{row.VirtualPath}",
                "The compact placement text repeats a scope label.",
                row.Placement));
        }

        var assetIdentities = scan.Assets
            .Select(asset => MusicSettingsAnalyzer.NormalizeMusicIdentity(asset.VirtualPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unresolvedAudioPaths = analysis.Settings
            .Where(setting => !IsGameDataOnlySetting(setting))
            .SelectMany(setting => setting.AudioPaths)
            .Where(path => !assetIdentities.Contains(MusicSettingsAnalyzer.NormalizeMusicIdentity(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unresolvedAudioPaths.Length > 0)
        {
            auditIssues.Add(new MusicAuditIssue(
                "UnresolvedAudioPath",
                "Warning",
                "Music Track audio paths",
                $"{unresolvedAudioPaths.Length} audio paths from plugin records were not found among scanned assets.",
                string.Join(Environment.NewLine, unresolvedAudioPaths)));
        }

        return new MusicAuditReport(
            "3",
            DateTimeOffset.UtcNow,
            scan.Profile.Mo2Root,
            scan.Profile.ProfileName,
            includeDisabledMods,
            scan.Mods.Count,
            scan.Plugins.Count,
            scan.Records.Count,
            analysis.Settings.Count,
            rows.Length,
            mappedRows.Length,
            rows.Length - mappedRows.Length,
            scan.Issues.Count,
            analysis.Issues.Count,
            duplicateGroups.Length,
            duplicateGroups.Sum(group => group.Count()),
            rows.Count(row => row.Flags.Contains("PlacementTooLong", StringComparer.Ordinal)),
            rows.Count(row => row.Flags.Contains("RepeatedPlacementLabel", StringComparer.Ordinal)),
            rows
                .GroupBy(row => row.ModName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            rows
                .GroupBy(row => row.SourceKind, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            mappedRows
                .GroupBy(row => row.ModName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            analysis.Settings
                .GroupBy(setting => setting.ScopeLabel, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            rows,
            recordRows,
            contextRecordRows,
            auditIssues);
    }

    private static bool IsGameDataOnlySetting(MusicSettingSource setting) =>
        setting.MusicTypeRecord.Plugin.ModName.Equals("Game Data", StringComparison.OrdinalIgnoreCase) &&
        setting.Tracks.All(track =>
            track.Record.Plugin.ModName.Equals("Game Data", StringComparison.OrdinalIgnoreCase));

    private static bool IsMusicRecord(PluginRecordSource record) =>
        record.RecordType.Equals("MusicType", StringComparison.OrdinalIgnoreCase) ||
        record.RecordType.Equals("MusicTrack", StringComparison.OrdinalIgnoreCase) ||
        record.RecordType.Equals("Cell", StringComparison.OrdinalIgnoreCase) &&
        record.References.Any(reference => reference.FieldName is "Music" or "Sounds.Music") ||
        record.RecordType.Equals("Location", StringComparison.OrdinalIgnoreCase) &&
        record.References.Any(reference => reference.FieldName is "Music" or "Sounds.Music") ||
        record.RecordType.Equals("Region", StringComparison.OrdinalIgnoreCase) &&
        record.References.Any(reference => reference.FieldName is "Music" or "Sounds.Music") ||
        record.RecordType.Equals("Worldspace", StringComparison.OrdinalIgnoreCase) &&
        record.References.Any(reference => reference.FieldName is "Music" or "Sounds.Music");

    private static bool IsMusicContextRecord(PluginRecordSource record) =>
        record.RecordType.Equals("Cell", StringComparison.OrdinalIgnoreCase) ||
        record.RecordType.Equals("Location", StringComparison.OrdinalIgnoreCase) ||
        record.RecordType.Equals("Region", StringComparison.OrdinalIgnoreCase) ||
        record.RecordType.Equals("Worldspace", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, PluginRecordSource> BuildRecordLookup(
        IReadOnlyList<PluginRecordSource> records) =>
        records
            .GroupBy(record => record.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(record => record.IsWinner)
                    .ThenByDescending(record => record.Plugin.LoadOrderIndex)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

    private static MusicAuditRecordRow BuildRecordRow(
        PluginRecordSource record,
        IReadOnlyDictionary<string, PluginRecordSource> recordsByFormKey) => new(
        record.FormKey,
        record.RecordType,
        record.EditorId,
        record.DisplayName,
        record.IsDeleted,
        record.IsWinner,
        record.Plugin.Name,
        record.Plugin.ModName,
        record.Plugin.Enabled,
        record.References
            .Select(reference => $"{reference.FieldName}:{reference.FormKey}")
            .ToArray(),
        record.Assets
            .Select(asset => $"{asset.FieldName}:{asset.VirtualPath}")
            .ToArray(),
        record.Conditions
            .Select(condition =>
            {
                var resolvedCondition = MusicConditionSource.From(condition, recordsByFormKey);
                var conditionRow = new MusicAuditConditionRow(
                    condition.FunctionName,
                    condition.CompareOperator,
                    condition.ComparisonValue,
                    condition.Flags,
                    condition.DataType,
                    condition.DataSummary)
                {
                    KeywordFormKey = resolvedCondition.KeywordFormKey,
                    KeywordEditorId = resolvedCondition.KeywordEditorId,
                    KeywordDisplayName = resolvedCondition.KeywordDisplayName,
                    KeywordJapaneseExplanation = resolvedCondition.KeywordJapaneseExplanation,
                    KeywordExplanationSource = resolvedCondition.KeywordExplanationSource,
                    KeywordDefinitionPluginName = resolvedCondition.KeywordDefinitionPluginName,
                    WeatherFormKey = resolvedCondition.WeatherFormKey,
                    WeatherEditorId = resolvedCondition.WeatherEditorId,
                    WeatherDisplayName = resolvedCondition.WeatherDisplayName,
                    WeatherJapaneseExplanation = resolvedCondition.WeatherJapaneseExplanation,
                    WeatherExplanationSource = resolvedCondition.WeatherExplanationSource,
                    WeatherDefinitionPluginName = resolvedCondition.WeatherDefinitionPluginName,
                    ComparisonValueType = resolvedCondition.ComparisonValueType,
                    ComparisonGlobalFormKey = resolvedCondition.ComparisonGlobalFormKey,
                    RunOnType = resolvedCondition.RunOnType,
                    RunOnTypeIndex = resolvedCondition.RunOnTypeIndex,
                    ReferenceFormKey = resolvedCondition.ReferenceFormKey,
                    UseAliases = resolvedCondition.UseAliases,
                    UsePackageData = resolvedCondition.UsePackageData,
                    FirstUnusedIntParameter = resolvedCondition.FirstUnusedIntParameter,
                    SecondUnusedIntParameter = resolvedCondition.SecondUnusedIntParameter,
                    FirstUnusedStringParameter = resolvedCondition.FirstUnusedStringParameter,
                    SecondUnusedStringParameter = resolvedCondition.SecondUnusedStringParameter
                };

                return conditionRow;
            })
            .ToArray());

    private static MusicAuditAssetRow BuildAssetRow(
        AssetSource asset,
        int index,
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlySet<string> duplicatePaths,
        int longPlacementThreshold)
    {
        var placement = MusicPlacementFormatter.Format(settings);
        var flags = new List<string>();
        if (settings.Count == 0)
        {
            flags.Add("Unmapped");
        }

        if (duplicatePaths.Contains(asset.VirtualPath))
        {
            flags.Add("DuplicateVirtualPath");
        }

        if (placement.Length > longPlacementThreshold)
        {
            flags.Add("PlacementTooLong");
        }

        if (HasRepeatedPlacementLabel(placement))
        {
            flags.Add("RepeatedPlacementLabel");
        }

        return new MusicAuditAssetRow(
            index,
            asset.ModName,
            asset.ModEnabled,
            asset.SourceKind.ToString(),
            asset.VirtualPath,
            asset.SourcePath,
            asset.ArchiveEntryPath,
            asset.Length,
            settings.Count == 0 ? "Unmapped" : "Mapped",
            settings.Count,
            settings
                .Select(setting => setting.ScopeLabel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            settings
                .Select(setting => $"{setting.ScopeLabel}:{setting.ScopeName}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            placement,
            flags);
    }

    private static bool HasRepeatedPlacementLabel(string placement)
    {
        var labels = new[] { "Music Type /", "WorldSpace /", "Location /", "Region /", "Cell /" };
        return labels.Any(label => CountOccurrences(placement, label) > 1);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var start = 0;
        while (true)
        {
            var index = text.IndexOf(value, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            start = index + value.Length;
        }
    }

    private static void AddScanIssues(
        ICollection<MusicAuditIssue> destination,
        IReadOnlyList<ScanIssue> issues,
        string code)
    {
        foreach (var issue in issues)
        {
            destination.Add(new MusicAuditIssue(
                code,
                issue.Severity.ToString(),
                issue.Scope,
                issue.Message,
                string.Join(
                    Environment.NewLine,
                    new[] { issue.SourcePath, issue.Detail }
                        .Where(value => !string.IsNullOrWhiteSpace(value)))));
        }
    }
}

public sealed record MusicAuditReportFiles(
    string JsonPath,
    string TsvPath,
    string RecordsTsvPath,
    string ContextRecordsTsvPath);

public static class MusicAuditReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static MusicAuditReportFiles Write(
        string outputDirectory,
        string fileStem,
        MusicAuditReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileStem);
        ArgumentNullException.ThrowIfNull(report);

        Directory.CreateDirectory(outputDirectory);
        var jsonPath = Path.Combine(outputDirectory, fileStem + ".json");
        var tsvPath = Path.Combine(outputDirectory, fileStem + ".tsv");
        var recordsTsvPath = Path.Combine(outputDirectory, fileStem + "-records.tsv");
        var contextRecordsTsvPath = Path.Combine(outputDirectory, fileStem + "-context-records.tsv");
        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(
            tsvPath,
            BuildTsv(report),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(
            recordsTsvPath,
            BuildRecordsTsv(report),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllText(
            contextRecordsTsvPath,
            BuildRecordsTsv(report.ContextRecords),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return new MusicAuditReportFiles(jsonPath, tsvPath, recordsTsvPath, contextRecordsTsvPath);
    }

    private static string BuildTsv(MusicAuditReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join('\t', new[]
        {
            "Index",
            "ModName",
            "ModEnabled",
            "SourceKind",
            "VirtualPath",
            "SourcePath",
            "ArchiveEntryPath",
            "Length",
            "MappingStatus",
            "SettingCount",
            "SettingScopes",
            "SettingNames",
            "Placement",
            "Flags"
        }));

        foreach (var row in report.Assets)
        {
            builder.AppendLine(string.Join('\t', new[]
            {
                row.Index.ToString(),
                Clean(row.ModName),
                row.ModEnabled.ToString(),
                Clean(row.SourceKind),
                Clean(row.VirtualPath),
                Clean(row.SourcePath),
                Clean(row.ArchiveEntryPath),
                row.Length?.ToString() ?? string.Empty,
                row.MappingStatus,
                row.SettingCount.ToString(),
                Clean(string.Join('|', row.SettingScopes)),
                Clean(string.Join('|', row.SettingNames)),
                Clean(row.Placement),
                Clean(string.Join('|', row.Flags))
            }));
        }

        return builder.ToString();
    }

    private static string BuildRecordsTsv(MusicAuditReport report) =>
        BuildRecordsTsv(report.Records);

    private static string BuildRecordsTsv(IReadOnlyList<MusicAuditRecordRow> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join('\t', new[]
        {
            "FormKey",
            "RecordType",
            "EditorId",
            "DisplayName",
            "IsDeleted",
            "IsWinner",
            "PluginName",
            "ModName",
            "PluginEnabled",
            "References",
            "Assets",
            "Conditions"
        }));

        foreach (var record in records)
        {
            builder.AppendLine(string.Join('\t', new[]
            {
                Clean(record.FormKey),
                Clean(record.RecordType),
                Clean(record.EditorId),
                Clean(record.DisplayName),
                record.IsDeleted.ToString(),
                record.IsWinner.ToString(),
                Clean(record.PluginName),
                Clean(record.ModName),
                record.PluginEnabled.ToString(),
                Clean(string.Join('|', record.References)),
                Clean(string.Join('|', record.Assets)),
                Clean(string.Join('|', record.Conditions.Select(FormatConditionTsv)))
            }));
        }

        return builder.ToString();
    }

    private static string FormatConditionTsv(MusicAuditConditionRow condition)
    {
        var text = $"{condition.FunctionName} {condition.CompareOperator} {condition.ComparisonValue}" +
                   $" Flags={condition.Flags} RunOn={condition.RunOnType}" +
                   $" RunOnIndex={condition.RunOnTypeIndex}";
        if (!string.IsNullOrWhiteSpace(condition.ReferenceFormKey))
        {
            text += $" Reference={condition.ReferenceFormKey}";
        }

        if (!string.IsNullOrWhiteSpace(condition.KeywordFormKey))
        {
            text += $" Keyword={condition.KeywordFormKey}";
            if (!string.IsNullOrWhiteSpace(condition.KeywordJapaneseExplanation))
            {
                text += $" KeywordName={condition.KeywordJapaneseExplanation}";
            }
        }

        if (!string.IsNullOrWhiteSpace(condition.WeatherFormKey))
        {
            text += $" Weather={condition.WeatherFormKey}";
            if (!string.IsNullOrWhiteSpace(condition.WeatherJapaneseExplanation))
            {
                text += $" WeatherName={condition.WeatherJapaneseExplanation}";
            }
        }

        return text;
    }

    private static string Clean(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
