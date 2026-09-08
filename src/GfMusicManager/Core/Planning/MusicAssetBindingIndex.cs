using GfMusicManager.Core.Analysis;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Planning;

public enum MusicAssetRole
{
    Unmapped,
    Primary,
    FinaleOnly
}

/// <summary>
/// Precomputed relationships for one normalized audio path.  Scan preparation,
/// the editable generation plan, and WPF rows share this data instead of
/// repeatedly matching every Track path and conflict for every asset.
/// </summary>
public sealed class MusicAssetBinding
{
    private readonly IReadOnlyList<MusicGenerationTrackPlan> _trackTemplates;

    internal MusicAssetBinding(
        MusicAssetRole role,
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyList<MusicTrackSource> tracks,
        IReadOnlyList<MusicConditionSource> conditions,
        IReadOnlyList<MusicDefinitionConflict> definitionConflicts,
        IReadOnlyList<MusicGenerationTrackPlan> trackTemplates)
    {
        Role = role;
        Settings = settings;
        Tracks = tracks;
        Conditions = conditions;
        DefinitionConflicts = definitionConflicts;
        _trackTemplates = trackTemplates;
    }

    public MusicAssetRole Role { get; }

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
                template.Conditions,
                template.FinaleAudioPath,
                template.FinaleAsset))
            .ToArray();
    }
}

public sealed class MusicAssetBindingIndex
{
    private static readonly MusicAssetBinding EmptyBinding = new(
        MusicAssetRole.Unmapped,
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

    public static MusicAssetBindingIndex Create(
        MusicAnalysisResult analysis,
        IReadOnlyList<AssetSource>? assets = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        var trackIndexes = BuildTrackIndexes(analysis.Settings);
        var conflictKeysBySetting = BuildConflictKeysBySetting(analysis.Settings);
        var settingsByTrackKey = analysis.Settings
            .SelectMany(setting => setting.Tracks.Select(track => new
            {
                TrackKey = MusicGenerationTrackKey.Create(track),
                Setting = setting
            }))
            .GroupBy(item => item.TrackKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MusicSettingSource>)group
                    .Select(item => item.Setting)
                    .DistinctBy(CreateSettingIdentity, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var bindings = new Dictionary<string, MusicAssetBinding>(
            StringComparer.OrdinalIgnoreCase);

        var candidatePaths = (assets?.Select(asset => asset.VirtualPath) ?? Array.Empty<string>())
            .Concat(analysis.SettingsByAssetPath.Keys)
            .Concat(analysis.Settings
                .SelectMany(setting => setting.Tracks)
                .SelectMany(track => track.MatchingFinaleAudioPaths))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in candidatePaths)
        {
            var matchingIdentity = NormalizeIdentity(path);
            var matchedTrackInfo = trackIndexes.ByAudioIdentity.TryGetValue(
                matchingIdentity,
                out var indexedTracks)
                ? indexedTracks
                : Array.Empty<TrackInfo>();
            var matchedFinaleTrackInfo = trackIndexes.ByFinaleIdentity.TryGetValue(
                matchingIdentity,
                out var indexedFinaleTracks)
                ? indexedFinaleTracks
                : Array.Empty<TrackInfo>();
            var mappedSettings = analysis.GetSettingsForAsset(path);
            var role = matchedTrackInfo.Count > 0 || mappedSettings.Count > 0
                ? MusicAssetRole.Primary
                : matchedFinaleTrackInfo.Count > 0
                    ? MusicAssetRole.FinaleOnly
                    : MusicAssetRole.Unmapped;
            var selectedTrackInfo = role == MusicAssetRole.Primary
                ? matchedTrackInfo
                : matchedFinaleTrackInfo;
            var tracks = selectedTrackInfo
                .Select(info => info.Track)
                .DistinctBy(track => MusicGenerationTrackKey.Create(track), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var settings = selectedTrackInfo
                .SelectMany(info => settingsByTrackKey.TryGetValue(info.TrackKey, out var matchedSettings)
                    ? matchedSettings
                    : Array.Empty<MusicSettingSource>())
                .Concat(mappedSettings)
                .DistinctBy(CreateSettingIdentity, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var conditions = tracks
                .SelectMany(track => track.Conditions)
                .DistinctBy(MusicConditionFormatter.CreateKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var trackTemplates = role == MusicAssetRole.Primary
                ? matchedTrackInfo
                .GroupBy(info => info.TrackKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => MusicGenerationTrackPlanFactory.Create(
                    group.First().Track,
                    assets))
                .OrderBy(track => track.TrackKey, StringComparer.OrdinalIgnoreCase)
                .ToArray()
                : Array.Empty<MusicGenerationTrackPlan>();
            var definitionConflicts = ResolveDefinitionConflicts(
                settings,
                analysis.DefinitionConflicts,
                conflictKeysBySetting);

            bindings[matchingIdentity] = new MusicAssetBinding(
                role,
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
        var identity = NormalizeIdentity(virtualPath);
        return _bindings.TryGetValue(identity, out var binding)
            ? binding
            : EmptyBinding;
    }

    private static TrackIndexes BuildTrackIndexes(
        IReadOnlyList<MusicSettingSource> settings)
    {
        var byDefinition = new Dictionary<string, TrackInfo>(StringComparer.OrdinalIgnoreCase);
        var byAudioIdentity = new Dictionary<string, List<TrackInfo>>(
            StringComparer.OrdinalIgnoreCase);
        var byFinaleIdentity = new Dictionary<string, List<TrackInfo>>(
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

            foreach (var identity in track.MatchingFinaleAudioPaths
                         .Select(NormalizeIdentity)
                         .Where(path => !string.IsNullOrWhiteSpace(path))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!byFinaleIdentity.TryGetValue(identity, out var matches))
                {
                    matches = new List<TrackInfo>();
                    byFinaleIdentity.Add(identity, matches);
                }

                matches.Add(info);
            }
        }

        return new TrackIndexes(
            byDefinition,
            byAudioIdentity.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<TrackInfo>)pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase),
            byFinaleIdentity.ToDictionary(
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

    private static string NormalizeIdentity(string path) =>
        MusicAudioPathIdentity.NormalizeMusicIdentity(path);

    private sealed record TrackInfo(
        string DefinitionIdentity,
        string TrackKey,
        MusicTrackSource Track,
        IReadOnlySet<string> NormalizedIdentities);

    private sealed record TrackIndexes(
        IReadOnlyDictionary<string, TrackInfo> ByDefinition,
        IReadOnlyDictionary<string, IReadOnlyList<TrackInfo>> ByAudioIdentity,
        IReadOnlyDictionary<string, IReadOnlyList<TrackInfo>> ByFinaleIdentity);

    private sealed record SettingConflictKeys(
        string MusicTypeFormKey,
        IReadOnlyList<string> OtherFormKeys);
}
