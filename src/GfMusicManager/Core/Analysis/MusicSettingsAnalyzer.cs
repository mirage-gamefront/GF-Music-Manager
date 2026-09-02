using SkyrimScan.Core.Models;
using GfMusicManager.Core.Diagnostics;

namespace GfMusicManager.Core.Analysis;

public sealed class MusicSettingsAnalyzer
{
    private static readonly IReadOnlyDictionary<string, MusicSettingScope> ScopeTypes =
        new Dictionary<string, MusicSettingScope>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cell"] = MusicSettingScope.Cell,
            ["Location"] = MusicSettingScope.Location,
            ["Region"] = MusicSettingScope.Region,
            ["Worldspace"] = MusicSettingScope.WorldSpace,
            ["WorldSpace"] = MusicSettingScope.WorldSpace
        };

    public MusicAnalysisResult Analyze(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Analyze(result.Records, result.Assets, result.Plugins);
    }

    public MusicAnalysisResult Analyze(
        IReadOnlyList<PluginRecordSource> records,
        IReadOnlyList<AssetSource> assets,
        IReadOnlyList<PluginSource>? plugins = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(assets);

        GfMusicManagerLog.Info(
            $"MusicSettingsAnalyzer.Analyze: begin. records={records.Count}, assets={assets.Count}.");

        var issues = new List<ScanIssue>();
        var recordsByFormKey = BuildRecordLookup(records);
        var candidateRecords = SelectCandidateRecords(records);
        var effectiveRecords = SelectEffectiveRecords(records);
        var additionalMusicProjectRepair = AdditionalMusicProjectRepairCatalog.Analyze(
            records,
            assets,
            plugins);
        var fantasySoundtrackProjectRepair = FantasySoundtrackProjectRepairCatalog.Analyze(
            records,
            assets,
            plugins);
        var audioPathRepairResolver = MusicAudioPathRepairResolver.Create(
            additionalMusicProjectRepair,
            fantasySoundtrackProjectRepair);
        var musicTypeRecords = candidateRecords
            .Where(record => IsRecordType(record, "MusicType"))
            .ToArray();
        var musicTypesByFormKey = musicTypeRecords
            .GroupBy(record => record.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PluginRecordSource>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var effectiveMusicTypesByFormKey = effectiveRecords
            .Where(record => IsRecordType(record, "MusicType"))
            .ToDictionary(record => record.FormKey, StringComparer.OrdinalIgnoreCase);
        var musicTrackRecords = candidateRecords
            .Where(record => IsRecordType(record, "MusicTrack"))
            .ToArray();
        var musicTracksByFormKey = musicTrackRecords
            .GroupBy(record => record.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PluginRecordSource>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var tracksByMusicTypeRecord = musicTypeRecords
            .ToDictionary(
                musicType => CreateRecordIdentity(musicType),
                musicType => ResolveTracks(
                    musicType,
                    musicTracksByFormKey,
                    issues,
                    recordsByFormKey,
                    audioPathRepairResolver),
                StringComparer.OrdinalIgnoreCase);
        var tracksByMusicTypeFormKey = musicTypeRecords
            .GroupBy(record => record.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MusicTrackSource>)group
                    .SelectMany(record => tracksByMusicTypeRecord[CreateRecordIdentity(record)])
                    .DistinctBy(CreateTrackIdentity, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var assetPathsByIdentity = assets
            .Select(asset => NormalizeAssetPath(asset.VirtualPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(NormalizeMusicIdentity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var settings = new List<MusicSettingSource>();
        foreach (var musicTypeRecord in musicTypeRecords)
        {
            settings.Add(new MusicSettingSource(
                MusicSettingScope.MusicType,
                musicTypeRecord.FormKey,
                musicTypeRecord.EditorId,
                musicTypeRecord.FormKey,
                musicTypeRecord.EditorId,
                musicTypeRecord,
                musicTypeRecord,
                tracksByMusicTypeRecord[CreateRecordIdentity(musicTypeRecord)]));
        }

        foreach (var scopeRecord in candidateRecords)
        {
            if (!ScopeTypes.TryGetValue(scopeRecord.RecordType, out var scope))
            {
                continue;
            }

            var musicLinks = scopeRecord.References
                .Where(reference => reference.FieldName is "Music" or "Sounds.Music")
                .ToArray();
            if (musicLinks.Length == 0)
            {
                continue;
            }

            if (musicLinks.Length > 1)
            {
                issues.Add(new ScanIssue(
                    ScanIssueSeverity.Warning,
                    "MusicSetting",
                    scopeRecord.Plugin.Path,
                    $"The {scopeRecord.RecordType} record contains multiple music settings.",
                    scopeRecord.FormKey));
            }

            foreach (var musicLink in musicLinks.DistinctBy(link => link.FormKey, StringComparer.OrdinalIgnoreCase))
            {
                if (!musicTypesByFormKey.TryGetValue(musicLink.FormKey, out var musicTypeDefinitions))
                {
                    issues.Add(new ScanIssue(
                        ScanIssueSeverity.Warning,
                        "MusicSetting",
                        scopeRecord.Plugin.Path,
                        "A music setting points to a Music Type that was not found in the scanned records.",
                        $"{scopeRecord.FormKey} -> {musicLink.FormKey}"));
                    continue;
                }

                var musicTypeRecord = effectiveMusicTypesByFormKey.TryGetValue(
                    musicLink.FormKey,
                    out var effectiveMusicType)
                    ? effectiveMusicType
                    : musicTypeDefinitions[0];

                settings.Add(new MusicSettingSource(
                    scope,
                    scopeRecord.FormKey,
                    scopeRecord.EditorId,
                    musicTypeRecord.FormKey,
                    musicTypeRecord.EditorId,
                    scopeRecord,
                    musicTypeRecord,
                    tracksByMusicTypeFormKey[musicLink.FormKey]));
            }
        }

        var settingsByAssetPath = settings
            .SelectMany(setting => setting.AudioPaths.Select(path =>
                (Identity: NormalizeMusicIdentity(path), Setting: setting)))
            .Where(item =>
                !string.IsNullOrWhiteSpace(item.Identity) &&
                assetPathsByIdentity.ContainsKey(item.Identity))
            .SelectMany(item => assetPathsByIdentity[item.Identity]
                .Select(path => (Path: path, item.Setting)))
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MusicSettingSource>)group
                    .Select(item => item.Setting)
                    .DistinctBy(CreateSettingIdentity, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var conditionCandidates = musicTrackRecords
            .SelectMany(record => record.Conditions)
            .Select(condition => MusicConditionSource.From(condition, recordsByFormKey))
            .GroupBy(MusicConditionFormatter.CreateKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(condition => condition.FunctionName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(condition => condition.ComparisonValue)
            .ToArray();
        var keywordCandidates = MusicCombatKeywordCandidateFilter.Select(
            effectiveRecords,
            conditionCandidates);
        var keywordRecordCount = effectiveRecords.Count(record => IsRecordType(record, "Keyword"));
        var weatherCandidates = candidateRecords
            .Where(record => IsRecordType(record, "Weather"))
            .GroupBy(record => record.FormKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(record => record.IsWinner)
                .ThenByDescending(record => record.Plugin.LoadOrderIndex)
                .First())
            .OrderBy(record => record.DisplayName ?? record.EditorId ?? record.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var effectiveSettings = settings
            .Where(setting => setting.Record.IsWinner && setting.MusicTypeRecord.IsWinner)
            .GroupBy(CreateSettingTargetIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var definitionConflicts = BuildDefinitionConflicts(candidateRecords, effectiveRecords);
        foreach (var conflict in definitionConflicts)
        {
            GfMusicManagerLog.Warning(
                $"MusicDefinitionConflict: type={conflict.RecordType}, formKey={conflict.FormKey}, " +
                $"definitions={string.Join(",", conflict.Definitions.Select(record => record.Plugin.Name))}, " +
                $"current={conflict.CurrentWinner.Plugin.Name}.");
        }
        var result = new MusicAnalysisResult(settings, settingsByAssetPath, issues)
        {
            AdditionalMusicProjectRepair = additionalMusicProjectRepair,
            FantasySoundtrackProjectRepair = fantasySoundtrackProjectRepair,
            ConditionCandidates = conditionCandidates,
            KeywordCandidates = keywordCandidates,
            WeatherCandidates = weatherCandidates,
            EffectiveSettings = effectiveSettings,
            DefinitionConflicts = definitionConflicts
        };
        GfMusicManagerLog.Info(
            $"MusicSettingsAnalyzer.Analyze: complete. candidateRecords={candidateRecords.Count}, " +
            $"effectiveRecords={effectiveRecords.Count}, musicTypes={musicTypeRecords.Length}, " +
            $"musicTracks={musicTrackRecords.Length}, settings={settings.Count}, " +
            $"effectiveSettings={effectiveSettings.Length}, definitionConflicts={definitionConflicts.Count}, " +
            $"mappedAssets={settingsByAssetPath.Count}, conditionCandidates={conditionCandidates.Length}, " +
            $"keywordRecords={keywordRecordCount}, combatKeywordCandidates={keywordCandidates.Count}, " +
            $"weatherCandidates={weatherCandidates.Length}, " +
            $"issues={issues.Count}, " +
            $"ampDetected={additionalMusicProjectRepair.IsDetected}, " +
            $"ampPathRepairs={additionalMusicProjectRepair.AudioPathRepairs.Count}, " +
            $"ampCombatTracks={additionalMusicProjectRepair.CombatTrackCount}, " +
            $"ampUnresolved={additionalMusicProjectRepair.UnresolvedAudioRepairs.Count}, " +
            $"fspDetected={fantasySoundtrackProjectRepair.IsDetected}, " +
            $"fspPathRepairs={fantasySoundtrackProjectRepair.AudioPathRepairs.Count}, " +
            $"fspUnresolved={fantasySoundtrackProjectRepair.UnresolvedAudioRepairs.Count}.");
        foreach (var repair in additionalMusicProjectRepair.AudioPathRepairs)
        {
            GfMusicManagerLog.Info(
                $"Additional Music Project repair: issue={repair.IssueId}, " +
                $"plugin={repair.PluginName}, record={repair.RecordEditorId ?? repair.RecordFormKey}, " +
                $"from={repair.OriginalAudioPath}, to={repair.RepairedAudioPath}.");
        }
        foreach (var unresolved in additionalMusicProjectRepair.UnresolvedAudioRepairs)
        {
            GfMusicManagerLog.Warning(
                $"Additional Music Project repair unresolved: {unresolved}.");
        }
        foreach (var repair in fantasySoundtrackProjectRepair.AudioPathRepairs)
        {
            GfMusicManagerLog.Info(
                $"Fantasy Soundtrack Project repair: " +
                $"plugin={repair.PluginName}, record={repair.RecordEditorId ?? repair.RecordFormKey}, " +
                $"from={repair.OriginalAudioPath}, to={repair.RepairedAudioPath}.");
        }
        foreach (var unresolved in fantasySoundtrackProjectRepair.UnresolvedAudioRepairs)
        {
            GfMusicManagerLog.Warning(
                $"Fantasy Soundtrack Project repair unresolved: {unresolved}.");
        }
        return result;
    }

    internal static string NormalizeAssetPath(string path)
        => MusicAudioPathIdentity.NormalizeAssetPath(path);

    internal static string NormalizeMusicIdentity(string path)
        => MusicAudioPathIdentity.NormalizeMusicIdentity(path);

    private static IReadOnlyList<PluginRecordSource> SelectCandidateRecords(
        IReadOnlyList<PluginRecordSource> records) =>
        records
            .Where(record => !record.IsDeleted)
            .ToArray();

    private static IReadOnlyList<PluginRecordSource> SelectEffectiveRecords(
        IReadOnlyList<PluginRecordSource> records)
    {
        return records
            .Where(record => !record.IsDeleted)
            .GroupBy(record => record.FormKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(record => record.IsWinner)
                .ThenByDescending(record => record.Plugin.LoadOrderIndex)
                .ThenByDescending(record => record.Plugin.ModPriority)
                .ThenByDescending(record => record.Plugin.Name, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToArray();
    }

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

    private static IReadOnlyList<MusicTrackSource> ResolveTracks(
        PluginRecordSource musicTypeRecord,
        IReadOnlyDictionary<string, IReadOnlyList<PluginRecordSource>> musicTracks,
        ICollection<ScanIssue> issues,
        IReadOnlyDictionary<string, PluginRecordSource> recordsByFormKey,
        MusicAudioPathRepairResolver audioPathRepairResolver)
    {
        var trackLinks = musicTypeRecord.References
            .Where(reference => reference.FieldName == "Tracks")
            .ToArray();

        var tracks = new List<MusicTrackSource>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trackLink in trackLinks.DistinctBy(link => link.FormKey, StringComparer.OrdinalIgnoreCase))
        {
            ResolveTrack(
                musicTypeRecord,
                trackLink.FormKey,
                musicTracks,
                tracks,
                issues,
                visited,
                recordsByFormKey,
                audioPathRepairResolver);
        }

        if (trackLinks.Length == 0)
        {
            issues.Add(new ScanIssue(
                ScanIssueSeverity.Warning,
                "MusicSetting",
                musicTypeRecord.Plugin.Path,
                "A Music Type does not contain any Music Track links.",
                musicTypeRecord.FormKey));
        }

        return tracks;
    }

    private static void ResolveTrack(
        PluginRecordSource musicTypeRecord,
        string trackFormKey,
        IReadOnlyDictionary<string, IReadOnlyList<PluginRecordSource>> musicTracks,
        ICollection<MusicTrackSource> tracks,
        ICollection<ScanIssue> issues,
        ISet<string> visited,
        IReadOnlyDictionary<string, PluginRecordSource> recordsByFormKey,
        MusicAudioPathRepairResolver audioPathRepairResolver)
    {
        if (!musicTracks.TryGetValue(trackFormKey, out var trackRecords))
        {
            issues.Add(new ScanIssue(
                ScanIssueSeverity.Warning,
                "MusicSetting",
                musicTypeRecord.Plugin.Path,
                "A Music Type points to a Music Track that was not found in the scanned records.",
                $"{musicTypeRecord.FormKey} -> {trackFormKey}"));
            return;
        }

        foreach (var trackRecord in trackRecords)
        {
            var trackIdentity = CreateRecordIdentity(trackRecord);
            if (!visited.Add(trackIdentity))
            {
                continue;
            }

            var audioPaths = trackRecord.Assets
                .Where(asset => asset.FieldName is "TrackFilename" or "FinaleFilename")
                .Select(asset => NormalizeAssetPath(asset.VirtualPath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var resolvedAudioPaths = audioPaths
                .SelectMany(audioPathRepairResolver.Resolve)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var childTrackLinks = trackRecord.References
                .Where(reference => reference.FieldName == "Tracks")
                .DistinctBy(reference => reference.FormKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var childTrackLink in childTrackLinks)
            {
                ResolveTrack(
                    musicTypeRecord,
                    childTrackLink.FormKey,
                    musicTracks,
                    tracks,
                    issues,
                    visited,
                    recordsByFormKey,
                    audioPathRepairResolver);
            }

            if (audioPaths.Length > 0)
            {
                tracks.Add(new MusicTrackSource(
                    trackRecord.FormKey,
                    trackRecord.EditorId,
                    audioPaths,
                    trackRecord)
                {
                    ResolvedAudioPaths = resolvedAudioPaths,
                    Conditions = trackRecord.Conditions
                        .Select(condition => MusicConditionSource.From(condition, recordsByFormKey))
                        .ToArray()
                });
            }
        }
    }

    private static IReadOnlyList<MusicDefinitionConflict> BuildDefinitionConflicts(
        IReadOnlyList<PluginRecordSource> candidateRecords,
        IReadOnlyList<PluginRecordSource> effectiveRecords)
    {
        var effectiveByKey = effectiveRecords
            .Where(IsMusicRecord)
            .ToDictionary(
                record => CreateRecordGroupKey(record),
                StringComparer.OrdinalIgnoreCase);

        return candidateRecords
            .Where(IsMusicRecord)
            .GroupBy(CreateRecordGroupKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var definitions = group
                    .OrderByDescending(record => record.IsWinner)
                    .ThenByDescending(record => record.Plugin.LoadOrderIndex)
                    .ThenBy(record => record.Plugin.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var winner = effectiveByKey.TryGetValue(group.Key, out var effective)
                    ? effective
                    : definitions[0];
                var first = definitions[0];
                return new MusicDefinitionConflict(
                    first.FormKey,
                    first.RecordType,
                    definitions,
                    winner);
            })
            .OrderBy(conflict => conflict.RecordType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(conflict => conflict.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsMusicRecord(PluginRecordSource record) =>
        IsRecordType(record, "MusicType") ||
        IsRecordType(record, "MusicTrack") ||
        ScopeTypes.ContainsKey(record.RecordType) &&
        record.References.Any(reference => reference.FieldName is "Music" or "Sounds.Music");

    private static string CreateRecordGroupKey(PluginRecordSource record) =>
        $"{record.RecordType}\u001f{record.FormKey}";

    private static string CreateRecordIdentity(PluginRecordSource record) =>
        string.Join(
            "\u001f",
            record.RecordType,
            record.FormKey,
            record.Plugin.Name,
            record.Plugin.Path);

    private static string CreateTrackIdentity(MusicTrackSource track) =>
        CreateRecordIdentity(track.Record);

    private static string CreateSettingIdentity(MusicSettingSource setting) =>
        string.Join(
            "\u001f",
            setting.Scope,
            setting.ScopeFormKey,
            setting.MusicTypeFormKey,
            setting.Record.Plugin.Name,
            setting.Record.Plugin.Path,
            setting.MusicTypeRecord.Plugin.Name,
            setting.MusicTypeRecord.Plugin.Path);

    private static string CreateSettingTargetIdentity(MusicSettingSource setting) =>
        string.Join(
            "\u001f",
            setting.Scope,
            setting.ScopeFormKey,
            setting.MusicTypeFormKey);

    private static bool IsRecordType(PluginRecordSource record, string typeName) =>
        record.RecordType.Equals(typeName, StringComparison.OrdinalIgnoreCase);
}
