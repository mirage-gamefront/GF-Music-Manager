using GfMusicManager.Core.Analysis;

namespace GfMusicManager.Core.Planning;

/// <summary>
/// Precomputed relationships for one normalized audio path.  Scan preparation,
/// the editable generation plan, and WPF rows share this data instead of
/// repeatedly matching every Track path and conflict for every asset.
/// </summary>
public sealed class MusicAssetBinding
{
    private readonly IReadOnlyList<MusicGenerationTrackPlan> _trackTemplates;

    internal MusicAssetBinding(
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyList<MusicTrackSource> tracks,
        IReadOnlyList<MusicConditionSource> conditions,
        IReadOnlyList<MusicDefinitionConflict> definitionConflicts,
        IReadOnlyList<MusicGenerationTrackPlan> trackTemplates)
    {
        Settings = settings;
        Tracks = tracks;
        Conditions = conditions;
        DefinitionConflicts = definitionConflicts;
        _trackTemplates = trackTemplates;
    }

    public IReadOnlyList<MusicSettingSource> Settings { get; }

    public IReadOnlyList<MusicTrackSource> Tracks { get; }

    public IReadOnlyList<MusicConditionSource> Conditions { get; }

    public IReadOnlyList<MusicDefinitionConflict> DefinitionConflicts { get; }

    public IReadOnlyList<MusicGenerationTrackPlan> CreateTrackPlans(string assetKey)
    {
        if (_trackTemplates.Count == 0)
        {
            return new[]
            {
                new MusicGenerationTrackPlan(
                    MusicGenerationTrackKey.CreateFallback(assetKey),
                    Conditions)
            };
        }

        return _trackTemplates
            .Select(template => new MusicGenerationTrackPlan(
                template.TrackKey,
                template.Conditions))
            .ToArray();
    }
}

public sealed class MusicAssetBindingIndex
{
    private static readonly MusicAssetBinding EmptyBinding = new(
        Array.Empty<MusicSettingSource>(),
        Array.Empty<MusicTrackSource>(),
        Array.Empty<MusicConditionSource>(),
        Array.Empty<MusicDefinitionConflict>(),
        Array.Empty<MusicGenerationTrackPlan>());

    private readonly IReadOnlyDictionary<string, MusicAssetBinding> _bindings;

    private MusicAssetBindingIndex(
        IReadOnlyDictionary<string, MusicAssetBinding> bindings)
    {
        _bindings = bindings;
    }

    public static MusicAssetBindingIndex Create(MusicAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var trackIndexes = BuildTrackIndexes(analysis.Settings);
        var conflictKeysBySetting = BuildConflictKeysBySetting(analysis.Settings);
        var bindings = new Dictionary<string, MusicAssetBinding>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var pair in analysis.SettingsByAssetPath)
        {
            var normalizedPath = NormalizePath(pair.Key);
            var settings = pair.Value;
            var matchingIdentity = NormalizeIdentity(pair.Key);
            var matchedTrackInfo = trackIndexes.ByAudioIdentity.TryGetValue(
                matchingIdentity,
                out var indexedTracks)
                ? indexedTracks
                : Array.Empty<TrackInfo>();
            var tracks = matchedTrackInfo
                .Select(info => info.Track)
                .ToArray();
            var conditions = tracks
                .SelectMany(track => track.Conditions)
                .DistinctBy(MusicConditionFormatter.CreateKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var trackTemplates = matchedTrackInfo
                .GroupBy(info => info.TrackKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => new MusicGenerationTrackPlan(
                    group.Key,
                    group.First().Track.Conditions))
                .OrderBy(track => track.TrackKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var definitionConflicts = ResolveDefinitionConflicts(
                settings,
                analysis.DefinitionConflicts,
                conflictKeysBySetting);

            bindings[normalizedPath] = new MusicAssetBinding(
                settings,
                tracks,
                conditions,
                definitionConflicts,
                trackTemplates);
        }

        return new MusicAssetBindingIndex(bindings);
    }

    public MusicAssetBinding Get(string virtualPath)
    {
        var normalizedPath = NormalizePath(virtualPath);
        return _bindings.TryGetValue(normalizedPath, out var binding)
            ? binding
            : EmptyBinding;
    }

    private static TrackIndexes BuildTrackIndexes(
        IReadOnlyList<MusicSettingSource> settings)
    {
        var byDefinition = new Dictionary<string, TrackInfo>(StringComparer.OrdinalIgnoreCase);
        var byAudioIdentity = new Dictionary<string, List<TrackInfo>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var track in settings.SelectMany(setting => setting.Tracks))
        {
            var definitionIdentity = MusicGenerationTrackKey.CreateDefinitionIdentity(track);
            if (byDefinition.ContainsKey(definitionIdentity))
            {
                continue;
            }

            var normalizedIdentities = track.MatchingAudioPaths
                .Select(NormalizeIdentity)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var info = new TrackInfo(
                definitionIdentity,
                MusicGenerationTrackKey.Create(track),
                track,
                normalizedIdentities);
            byDefinition.Add(definitionIdentity, info);
            foreach (var identity in normalizedIdentities)
            {
                if (!byAudioIdentity.TryGetValue(identity, out var matches))
                {
                    matches = new List<TrackInfo>();
                    byAudioIdentity.Add(identity, matches);
                }

                matches.Add(info);
            }
        }

        return new TrackIndexes(
            byDefinition,
            byAudioIdentity.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<TrackInfo>)pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, SettingConflictKeys> BuildConflictKeysBySetting(
        IReadOnlyList<MusicSettingSource> settings)
    {
        var result = new Dictionary<string, SettingConflictKeys>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            var identity = CreateSettingIdentity(setting);
            if (result.ContainsKey(identity))
            {
                continue;
            }

            result.Add(
                identity,
                new SettingConflictKeys(
                    setting.MusicTypeFormKey,
                    setting.Tracks
                        .Select(track => track.FormKey)
                        .Prepend(setting.Record.FormKey)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray()));
        }

        return result;
    }

    private static IReadOnlyList<MusicDefinitionConflict> ResolveDefinitionConflicts(
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyList<MusicDefinitionConflict> conflicts,
        IReadOnlyDictionary<string, SettingConflictKeys> conflictKeysBySetting)
    {
        if (settings.Count == 0)
        {
            return Array.Empty<MusicDefinitionConflict>();
        }

        var musicTypeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var otherKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var setting in settings)
        {
            if (!conflictKeysBySetting.TryGetValue(
                    CreateSettingIdentity(setting),
                    out var keys))
            {
                continue;
            }

            musicTypeKeys.Add(keys.MusicTypeFormKey);
            foreach (var key in keys.OtherFormKeys)
            {
                otherKeys.Add(key);
            }
        }

        return conflicts
            .Where(conflict =>
                conflict.RecordType.Equals("MusicType", StringComparison.OrdinalIgnoreCase)
                    ? musicTypeKeys.Contains(conflict.FormKey)
                    : otherKeys.Contains(conflict.FormKey))
            .ToArray();
    }

    private static string CreateSettingIdentity(MusicSettingSource setting) => string.Join(
        "\u001f",
        setting.Scope,
        setting.ScopeFormKey,
        setting.MusicTypeFormKey,
        setting.Record.Plugin.Path,
        setting.MusicTypeRecord.Plugin.Path);

    private static string NormalizePath(string path) =>
        MusicSettingsAnalyzer.NormalizeAssetPath(path);

    private static string NormalizeIdentity(string path) =>
        MusicAudioPathIdentity.NormalizeMusicIdentity(path);

    private sealed record TrackInfo(
        string DefinitionIdentity,
        string TrackKey,
        MusicTrackSource Track,
        IReadOnlySet<string> NormalizedIdentities);

    private sealed record TrackIndexes(
        IReadOnlyDictionary<string, TrackInfo> ByDefinition,
        IReadOnlyDictionary<string, IReadOnlyList<TrackInfo>> ByAudioIdentity);

    private sealed record SettingConflictKeys(
        string MusicTypeFormKey,
        IReadOnlyList<string> OtherFormKeys);
}
