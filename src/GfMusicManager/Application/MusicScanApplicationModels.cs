using System.Text.Json;
using System.Text.Json.Serialization;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Application;

/// <summary>
/// GUI and CLI share this request model so scan options do not depend on WPF controls.
/// </summary>
public sealed record MusicScanRequest
{
    public required string Mo2Root { get; init; }

    public string? ProfileName { get; init; }

    public bool IncludeDisabledMods { get; init; }

    public bool ReadPluginRecords { get; init; } = true;

    public bool ScanArchives { get; init; } = true;

    public bool ScanLooseAssets { get; init; } = true;

    public bool IncludeGeneratedProduct { get; init; }

    public IReadOnlyList<string> AssetExtensions { get; init; } =
        ScanOptions.DefaultAssetExtensions.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<string> ExcludedModNames { get; init; } =
        Array.Empty<string>();

    public ScanOptions ToCoreOptions()
    {
        var excludedModNames = ExcludedModNames
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!IncludeGeneratedProduct)
        {
            excludedModNames.Add("GF Music Product");
        }

        return new ScanOptions
        {
            Mo2Root = Mo2Root,
            ProfileName = ProfileName,
            IncludeDisabledMods = IncludeDisabledMods,
            ReadPluginRecords = ReadPluginRecords,
            IncludedRecordTypes = ReadPluginRecords
                ? MusicScanRecordTypeCatalog.RequiredRecordTypes
                : null,
            RetainOnlyMusicAssignments = ReadPluginRecords,
            ScanArchives = ScanArchives,
            ScanLooseAssets = ScanLooseAssets,
            AssetExtensions = AssetExtensions
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            ExcludedModNames = excludedModNames
        };
    }
}

/// <summary>
/// Record types required to analyze Skyrim music.  General-purpose scan
/// callers can still omit the filter and retain every record; the music
/// application deliberately reads only this set into memory.
/// </summary>
public static class MusicScanRecordTypeCatalog
{
    public static IReadOnlySet<string> RequiredRecordTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MusicType",
            "MusicTrack",
            "Cell",
            "Location",
            "Region",
            "Worldspace",
            "Keyword",
            "Weather"
        };
}

public sealed record MusicScanApplicationResult(
    MusicScanRequest Request,
    ScanResult Scan,
    MusicAnalysisResult MusicAnalysis,
    AudioDuplicateAnalysisResult AudioDuplicates);

/// <summary>
/// Versioned, machine-readable scan output consumed by later plan/generate
/// commands.  The JSON stores a normalized snapshot instead of serializing the
/// object graph used by the WPF view model.  In particular, settings reference
/// Track and record keys rather than embedding the same records repeatedly.
/// </summary>
public sealed record MusicScanResultDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    MusicScanResultPayload Payload)
{
    public const int CurrentSchemaVersion = 3;

    private MusicScanApplicationResult? _result;

    [JsonIgnore]
    public MusicScanApplicationResult Result => _result ??= Payload.ToCoreResult();

    public static MusicScanResultDocument Create(MusicScanApplicationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(
            CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            MusicScanResultPayload.Create(result));
    }
}

public sealed record MusicScanResultPayload(
    MusicScanRequest Request,
    MusicScanSnapshot Scan,
    MusicAnalysisSnapshot MusicAnalysis,
    AudioDuplicateSnapshot AudioDuplicates)
{
    public static MusicScanResultPayload Create(MusicScanApplicationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(
            result.Request,
            MusicScanSnapshot.Create(result.Scan),
            MusicAnalysisSnapshot.Create(result.MusicAnalysis),
            AudioDuplicateSnapshot.Create(result.AudioDuplicates));
    }

    public MusicScanApplicationResult ToCoreResult()
    {
        var scan = Scan.ToCore();
        var recordsByKey = MusicScanPersistenceKeys.CreateRecordLookup(scan.Records);
        var analysis = MusicAnalysis.ToCore(recordsByKey);
        var duplicates = AudioDuplicates.ToCore(scan.Assets);
        return new MusicScanApplicationResult(Request, scan, analysis, duplicates);
    }
}

public sealed record MusicScanSnapshot(
    Mo2ProfileSnapshot Profile,
    IReadOnlyList<ModSource> Mods,
    IReadOnlyList<PluginSource> Plugins,
    IReadOnlyList<MusicScanRecordSnapshot> Records,
    IReadOnlyList<AssetSource> Assets,
    IReadOnlyList<ScanIssue> Issues)
{
    public static MusicScanSnapshot Create(ScanResult scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        return new(
            scan.Profile,
            scan.Mods,
            scan.Plugins,
            scan.Records
                .Where(record => MusicScanRecordTypeCatalog.RequiredRecordTypes.Contains(record.RecordType))
                .Select(MusicScanRecordSnapshot.Create)
                .ToArray(),
            scan.Assets,
            scan.Issues);
    }

    public ScanResult ToCore() => new(
        Profile,
        Mods,
        Plugins,
        Records.Select(record => record.ToCore()).ToArray(),
        Assets,
        Issues);
}

public sealed record MusicScanRecordSnapshot(
    string FormKey,
    string RecordType,
    string? EditorId,
    bool IsDeleted,
    PluginSource Plugin,
    bool IsWinner,
    string? DisplayName,
    IReadOnlyList<PluginRecordReferenceSource> References,
    IReadOnlyList<PluginRecordAssetSource> Assets,
    IReadOnlyList<PluginRecordConditionSource> Conditions)
{
    public static MusicScanRecordSnapshot Create(PluginRecordSource record) => new(
        record.FormKey,
        record.RecordType,
        record.EditorId,
        record.IsDeleted,
        record.Plugin,
        record.IsWinner,
        record.DisplayName,
        record.References,
        record.Assets,
        record.Conditions);

    public PluginRecordSource ToCore() => new(
        FormKey,
        RecordType,
        EditorId,
        IsDeleted,
        Plugin,
        IsWinner)
    {
        DisplayName = DisplayName,
        References = References,
        Assets = Assets,
        Conditions = Conditions
    };
}

public sealed record MusicAnalysisSnapshot(
    IReadOnlyList<MusicSettingSnapshot> Settings,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SettingsByAssetPath,
    IReadOnlyList<ScanIssue> Issues,
    AdditionalMusicProjectRepairReport AdditionalMusicProjectRepair,
    FantasySoundtrackProjectRepairReport FantasySoundtrackProjectRepair,
    IReadOnlyList<MusicConditionSource> ConditionCandidates,
    IReadOnlyList<string> KeywordCandidateRecordKeys,
    IReadOnlyList<string> WeatherCandidateRecordKeys,
    IReadOnlyList<string> EffectiveSettingKeys,
    IReadOnlyList<MusicDefinitionConflictSnapshot> DefinitionConflicts,
    IReadOnlyList<MusicTrackSnapshot> Tracks)
{
    public static MusicAnalysisSnapshot Create(MusicAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var settings = analysis.Settings
            .Select(MusicSettingSnapshot.Create)
            .ToArray();
        var tracks = analysis.Settings
            .SelectMany(setting => setting.Tracks)
            .GroupBy(MusicScanPersistenceKeys.CreateTrackKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => MusicTrackSnapshot.Create(group.First()))
            .ToArray();
        var settingKeys = settings
            .Select(setting => setting.Identity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var settingsByAssetPath = analysis.SettingsByAssetPath
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value
                    .Select(MusicScanPersistenceKeys.CreateSettingKey)
                    .Where(settingKeys.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return new(
            settings,
            settingsByAssetPath,
            analysis.Issues,
            analysis.AdditionalMusicProjectRepair,
            analysis.FantasySoundtrackProjectRepair,
            analysis.ConditionCandidates,
            analysis.KeywordCandidates
                .Select(MusicScanPersistenceKeys.CreateRecordKey)
                .ToArray(),
            analysis.WeatherCandidates
                .Select(MusicScanPersistenceKeys.CreateRecordKey)
                .ToArray(),
            analysis.EffectiveSettings
                .Select(MusicScanPersistenceKeys.CreateSettingKey)
                .Where(settingKeys.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            analysis.DefinitionConflicts
                .Select(MusicDefinitionConflictSnapshot.Create)
                .ToArray(),
            tracks);
    }

    public MusicAnalysisResult ToCore(
        IReadOnlyDictionary<string, PluginRecordSource> recordsByKey)
    {
        var tracksByKey = Tracks
            .Select(snapshot => snapshot.ToCore(recordsByKey))
            .ToDictionary(
                track => MusicScanPersistenceKeys.CreateTrackKey(track),
                StringComparer.OrdinalIgnoreCase);
        var settings = Settings
            .Select(snapshot => snapshot.ToCore(recordsByKey, tracksByKey))
            .ToArray();
        var settingsByKey = settings.ToDictionary(
            MusicScanPersistenceKeys.CreateSettingKey,
            StringComparer.OrdinalIgnoreCase);
        var settingsByAssetPath = SettingsByAssetPath.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MusicSettingSource>)pair.Value
                .Select(key => Resolve(settingsByKey, key, "Music setting"))
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var effectiveSettings = EffectiveSettingKeys
            .Select(key => Resolve(settingsByKey, key, "Effective music setting"))
            .ToArray();
        var keywordCandidates = KeywordCandidateRecordKeys
            .Select(key => Resolve(recordsByKey, key, "Keyword candidate"))
            .ToArray();
        var weatherCandidates = WeatherCandidateRecordKeys
            .Select(key => Resolve(recordsByKey, key, "Weather candidate"))
            .ToArray();

        return new MusicAnalysisResult(settings, settingsByAssetPath, Issues)
        {
            AdditionalMusicProjectRepair = AdditionalMusicProjectRepair,
            FantasySoundtrackProjectRepair = FantasySoundtrackProjectRepair,
            ConditionCandidates = ConditionCandidates,
            KeywordCandidates = keywordCandidates,
            WeatherCandidates = weatherCandidates,
            EffectiveSettings = effectiveSettings,
            DefinitionConflicts = DefinitionConflicts
                .Select(conflict => conflict.ToCore(recordsByKey))
                .ToArray()
        };
    }

    private static TValue Resolve<TValue>(
        IReadOnlyDictionary<string, TValue> values,
        string key,
        string kind)
    {
        if (values.TryGetValue(key, out var value))
        {
            return value;
        }

        throw new InvalidDataException($"{kind} reference is missing from scan result: {key}");
    }
}

public sealed record MusicSettingSnapshot(
    string Identity,
    MusicSettingScope Scope,
    string ScopeFormKey,
    string? ScopeEditorId,
    string MusicTypeFormKey,
    string? MusicTypeEditorId,
    string RecordKey,
    string MusicTypeRecordKey,
    IReadOnlyList<string> TrackKeys)
{
    public static MusicSettingSnapshot Create(MusicSettingSource setting) => new(
        MusicScanPersistenceKeys.CreateSettingKey(setting),
        setting.Scope,
        setting.ScopeFormKey,
        setting.ScopeEditorId,
        setting.MusicTypeFormKey,
        setting.MusicTypeEditorId,
        MusicScanPersistenceKeys.CreateRecordKey(setting.Record),
        MusicScanPersistenceKeys.CreateRecordKey(setting.MusicTypeRecord),
        setting.Tracks
            .Select(MusicScanPersistenceKeys.CreateTrackKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());

    public MusicSettingSource ToCore(
        IReadOnlyDictionary<string, PluginRecordSource> recordsByKey,
        IReadOnlyDictionary<string, MusicTrackSource> tracksByKey) =>
        new(
            Scope,
            ScopeFormKey,
            ScopeEditorId,
            MusicTypeFormKey,
            MusicTypeEditorId,
            Resolve(recordsByKey, RecordKey, "Setting record"),
            Resolve(recordsByKey, MusicTypeRecordKey, "Music Type record"),
            TrackKeys
                .Select(key => Resolve(tracksByKey, key, "Music Track"))
                .ToArray());

    private static TValue Resolve<TValue>(
        IReadOnlyDictionary<string, TValue> values,
        string key,
        string kind)
    {
        if (values.TryGetValue(key, out var value))
        {
            return value;
        }

        throw new InvalidDataException($"{kind} reference is missing from scan result: {key}");
    }
}

public sealed record MusicTrackSnapshot(
    string Identity,
    string FormKey,
    string? EditorId,
    string RecordKey,
    IReadOnlyList<string> AudioPaths,
    IReadOnlyList<string> ResolvedAudioPaths,
    IReadOnlyList<MusicConditionSource> Conditions)
{
    public static MusicTrackSnapshot Create(MusicTrackSource track) => new(
        MusicScanPersistenceKeys.CreateTrackKey(track),
        track.FormKey,
        track.EditorId,
        MusicScanPersistenceKeys.CreateRecordKey(track.Record),
        track.AudioPaths,
        track.ResolvedAudioPaths,
        track.Conditions);

    public MusicTrackSource ToCore(
        IReadOnlyDictionary<string, PluginRecordSource> recordsByKey)
    {
        if (!recordsByKey.TryGetValue(RecordKey, out var record))
        {
            throw new InvalidDataException($"Music Track record is missing from scan result: {RecordKey}");
        }

        return new MusicTrackSource(FormKey, EditorId, AudioPaths, record)
        {
            ResolvedAudioPaths = ResolvedAudioPaths,
            Conditions = Conditions
        };
    }
}

public sealed record MusicDefinitionConflictSnapshot(
    string FormKey,
    string RecordType,
    IReadOnlyList<string> DefinitionRecordKeys,
    string CurrentWinnerRecordKey)
{
    public static MusicDefinitionConflictSnapshot Create(MusicDefinitionConflict conflict) => new(
        conflict.FormKey,
        conflict.RecordType,
        conflict.Definitions.Select(MusicScanPersistenceKeys.CreateRecordKey).ToArray(),
        MusicScanPersistenceKeys.CreateRecordKey(conflict.CurrentWinner));

    public MusicDefinitionConflict ToCore(
        IReadOnlyDictionary<string, PluginRecordSource> recordsByKey)
    {
        return new MusicDefinitionConflict(
            FormKey,
            RecordType,
            DefinitionRecordKeys.Select(key => Resolve(recordsByKey, key)).ToArray(),
            Resolve(recordsByKey, CurrentWinnerRecordKey));
    }

    private static PluginRecordSource Resolve(
        IReadOnlyDictionary<string, PluginRecordSource> recordsByKey,
        string key)
    {
        if (recordsByKey.TryGetValue(key, out var record))
        {
            return record;
        }

        throw new InvalidDataException($"Definition conflict record is missing from scan result: {key}");
    }
}

public sealed record AudioDuplicateSnapshot(
    IReadOnlyList<AudioDuplicateGroupSnapshot> Groups,
    int AnalyzedSourceCount,
    int ReadFailureCount,
    int SimilarityComparisonCount,
    bool SimilarityDecoderAvailable)
{
    public static AudioDuplicateSnapshot Create(AudioDuplicateAnalysisResult result) => new(
        result.Groups.Select(AudioDuplicateGroupSnapshot.Create).ToArray(),
        result.AnalyzedSourceCount,
        result.ReadFailureCount,
        result.SimilarityComparisonCount,
        result.SimilarityDecoderAvailable);

    public AudioDuplicateAnalysisResult ToCore(IReadOnlyList<AssetSource> assets)
    {
        var assetsByKey = assets.ToDictionary(
            MusicGenerationPlanEntry.CreateAssetKey,
            StringComparer.OrdinalIgnoreCase);
        return new AudioDuplicateAnalysisResult(
            Groups.Select(group => group.ToCore(assetsByKey)).ToArray(),
            AnalyzedSourceCount,
            ReadFailureCount,
            SimilarityComparisonCount,
            SimilarityDecoderAvailable);
    }
}

public sealed record AudioDuplicateGroupSnapshot(
    string GroupId,
    AudioDuplicateKind Kind,
    string Subject,
    string Explanation,
    string DetectionMethod,
    IReadOnlyList<AudioDuplicateSourceSnapshot> Sources,
    double? SimilarityScore)
{
    public static AudioDuplicateGroupSnapshot Create(AudioDuplicateGroup group) => new(
        group.GroupId,
        group.Kind,
        group.Subject,
        group.Explanation,
        group.DetectionMethod,
        group.Sources.Select(AudioDuplicateSourceSnapshot.Create).ToArray(),
        group.SimilarityScore);

    public AudioDuplicateGroup ToCore(
        IReadOnlyDictionary<string, AssetSource> assetsByKey) =>
        new(
            GroupId,
            Kind,
            Subject,
            Explanation,
            DetectionMethod,
            Sources.Select(source => source.ToCore(assetsByKey)).ToArray(),
            SimilarityScore);
}

public sealed record AudioDuplicateSourceSnapshot(
    string AssetKey,
    string ContentHash,
    double? DurationSeconds)
{
    public static AudioDuplicateSourceSnapshot Create(AudioDuplicateSource source) => new(
        source.AssetKey,
        source.ContentHash,
        source.DurationSeconds);

    public AudioDuplicateSource ToCore(
        IReadOnlyDictionary<string, AssetSource> assetsByKey)
    {
        if (!assetsByKey.TryGetValue(AssetKey, out var asset))
        {
            throw new InvalidDataException($"Duplicate source asset is missing from scan result: {AssetKey}");
        }

        return new AudioDuplicateSource(asset, ContentHash, DurationSeconds);
    }
}

internal static class MusicScanPersistenceKeys
{
    public static string CreateRecordKey(PluginRecordSource record) => string.Join(
        "\u001f",
        record.RecordType,
        record.FormKey,
        record.Plugin.Name,
        record.Plugin.Path);

    public static string CreateTrackKey(MusicTrackSource track) =>
        CreateRecordKey(track.Record);

    public static string CreateSettingKey(MusicSettingSource setting) => string.Join(
        "\u001f",
        setting.Scope,
        setting.ScopeFormKey,
        setting.MusicTypeFormKey,
        setting.Record.Plugin.Name,
        setting.Record.Plugin.Path,
        setting.MusicTypeRecord.Plugin.Name,
        setting.MusicTypeRecord.Plugin.Path);

    public static IReadOnlyDictionary<string, PluginRecordSource> CreateRecordLookup(
        IReadOnlyList<PluginRecordSource> records) => records
        .GroupBy(CreateRecordKey, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group.First(),
            StringComparer.OrdinalIgnoreCase);
}

public static class MusicScanResultJson
{
    public static JsonSerializerOptions CreateOptions(bool writeIndented = true) => new()
    {
        WriteIndented = writeIndented,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public static void Save(string path, MusicScanApplicationResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(result);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = MusicScanResultDocument.Create(result);
        using var stream = File.Create(fullPath);
        JsonSerializer.Serialize(stream, document, CreateOptions(writeIndented: false));
    }

    public static MusicScanResultDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        using var stream = File.OpenRead(fullPath);
        var document = JsonSerializer.Deserialize<MusicScanResultDocument>(
            stream,
            CreateOptions(writeIndented: false));
        if (document is null)
        {
            throw new InvalidDataException($"Scan result is empty or invalid: {fullPath}");
        }

        if (document.SchemaVersion != MusicScanResultDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported scan result schema version: {document.SchemaVersion}");
        }

        return document;
    }
}
