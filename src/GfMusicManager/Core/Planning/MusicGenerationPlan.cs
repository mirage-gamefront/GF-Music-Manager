using GfMusicManager.Core.Analysis;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Planning;

/// <summary>
/// Holds user-owned generation decisions separately from read-only scan results.
/// </summary>
public sealed class MusicGenerationPlan
{
    private readonly Dictionary<string, MusicGenerationPlanEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MusicGenerationPlanEntry> _orderedEntries = new();

    public IReadOnlyList<MusicGenerationPlanEntry> Entries => _orderedEntries;

    public IReadOnlyList<MusicGenerationPlanConflict> Conflicts => BuildConflicts();

    public bool? KeepVanillaMusic { get; set; }

    public MusicGenerationPlanEntry GetOrCreate(
        AssetSource asset,
        IReadOnlyList<MusicSettingSource> initialDestinations,
        IReadOnlyList<MusicConditionSource>? initialConditions = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(initialDestinations);

        var key = MusicGenerationPlanEntry.CreateAssetKey(asset);
        if (_entries.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var entry = new MusicGenerationPlanEntry(asset, true, initialDestinations, initialConditions);
        _entries.Add(key, entry);
        _orderedEntries.Add(entry);
        return entry;
    }

    public MusicGenerationPlanEntry GetOrCreatePrepared(
        AssetSource asset,
        IReadOnlyList<MusicSettingSource> initialDestinations,
        IReadOnlyList<MusicGenerationTrackPlan> initialTracks)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(initialDestinations);
        ArgumentNullException.ThrowIfNull(initialTracks);

        var key = MusicGenerationPlanEntry.CreateAssetKey(asset);
        if (_entries.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var entry = MusicGenerationPlanEntry.CreatePrepared(
            asset,
            true,
            initialDestinations,
            initialTracks);
        _entries.Add(key, entry);
        _orderedEntries.Add(entry);
        return entry;
    }

    public void Clear()
    {
        _entries.Clear();
        _orderedEntries.Clear();
        KeepVanillaMusic = null;
    }

    private IReadOnlyList<MusicGenerationPlanConflict> BuildConflicts()
    {
        var conflicts = new List<MusicGenerationPlanConflict>();

        foreach (var group in _orderedEntries
                     .Where(entry => entry.Asset is not null)
                     .GroupBy(
                         entry => NormalizeVirtualPath(entry.Asset!.VirtualPath),
                         StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1 &&
                                     group.Select(entry => entry.Asset!.ModName)
                                         .Distinct(StringComparer.OrdinalIgnoreCase)
                                         .Count() > 1))
        {
            conflicts.Add(new MusicGenerationPlanConflict(
                MusicGenerationPlanConflictKind.DuplicateVirtualPath,
                group.Key,
                "同じゲーム内パスに重複音源があります。競合で負けたファイルのみGF Music Productへコピーし、利用できるようにします。",
                group.ToArray()));
        }

        foreach (var group in _orderedEntries
                     .Where(entry => entry.IsAdopted)
                     .SelectMany(entry => entry.DestinationKeys.Select(destination =>
                         (Destination: destination, Entry: entry)))
                     .GroupBy(
                         item => string.Join(
                             "\u001f",
                             item.Destination.Scope,
                             item.Destination.ScopeFormKey),
                         StringComparer.OrdinalIgnoreCase)
                     .Where(group => group
                         .Select(item => item.Destination.MusicTypeFormKey)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Count() > 1))
        {
            var entries = group.Select(item => item.Entry).Distinct().ToArray();
            var destination = group.First().Destination;
            var scopeLabel = destination.Scope switch
            {
                MusicSettingScope.Cell => "Cell",
                MusicSettingScope.Location => "Location",
                MusicSettingScope.Region => "Region",
                MusicSettingScope.WorldSpace => "WorldSpace",
                MusicSettingScope.MusicType => "Music Type",
                _ => destination.Scope.ToString()
            };
            conflicts.Add(new MusicGenerationPlanConflict(
                MusicGenerationPlanConflictKind.MultipleGeneratedMusicTypesForRecord,
                group.Key.Replace('\u001f', ':'),
                $"同じ{scopeLabel}に異なるMusic Typeが割り当てられています。適用先ごとに統合用Music Typeを作成し、採用Trackを集約します。",
                entries,
                destination.Scope,
                destination.ScopeFormKey));
        }

        return conflicts;
    }

    private static string NormalizeVirtualPath(string path) => path
        .Replace('/', '\\')
        .TrimStart('\\');
}

public sealed class MusicGenerationPlanEntry
{
    private IReadOnlyList<MusicSettingKey> _destinationKeys;
    private IReadOnlyList<MusicGenerationTrackPlan> _tracks;

    public MusicGenerationPlanEntry(
        AssetSource asset,
        bool isAdopted,
        IEnumerable<MusicSettingSource> initialDestinations,
        IEnumerable<MusicConditionSource>? initialConditions = null)
        : this(
            asset,
            isAdopted,
            MaterializeDestinations(initialDestinations),
            initialConditions,
            useAssetPath: true)
    {
    }

    public static MusicGenerationPlanEntry CreatePrepared(
        AssetSource asset,
        bool isAdopted,
        IReadOnlyList<MusicSettingSource> initialDestinations,
        IReadOnlyList<MusicGenerationTrackPlan> initialTracks) =>
        new(asset, isAdopted, initialDestinations, initialTracks);

    private MusicGenerationPlanEntry(
        AssetSource asset,
        bool isAdopted,
        IReadOnlyList<MusicSettingSource> initialDestinations,
        IReadOnlyList<MusicGenerationTrackPlan> initialTracks)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(initialDestinations);
        ArgumentNullException.ThrowIfNull(initialTracks);
        Asset = asset;
        AssetKey = CreateAssetKey(asset);
        IsAdopted = isAdopted;
        _destinationKeys = CreateKeys(initialDestinations);
        _tracks = initialTracks
            .Select(track => new MusicGenerationTrackPlan(track.TrackKey, track.Conditions))
            .ToArray();
    }

    private MusicGenerationPlanEntry(
        AssetSource asset,
        bool isAdopted,
        IReadOnlyList<MusicSettingSource> initialDestinations,
        IEnumerable<MusicConditionSource>? initialConditions,
        bool useAssetPath)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(initialDestinations);
        Asset = asset;
        AssetKey = CreateAssetKey(asset);
        IsAdopted = isAdopted;
        _destinationKeys = CreateKeys(initialDestinations);
        _tracks = MusicGenerationTrackPlanFactory.Create(
            asset,
            initialDestinations,
            initialConditions);
    }

    public MusicGenerationPlanEntry(
        string assetKey,
        bool isAdopted,
        IEnumerable<MusicSettingSource> initialDestinations,
        IEnumerable<MusicConditionSource>? initialConditions = null)
    {
        if (string.IsNullOrWhiteSpace(assetKey))
        {
            throw new ArgumentException("音源キーが空です。", nameof(assetKey));
        }

        ArgumentNullException.ThrowIfNull(initialDestinations);
        AssetKey = assetKey;
        IsAdopted = isAdopted;
        _destinationKeys = CreateKeys(initialDestinations);
        _tracks = MusicGenerationTrackPlanFactory.Create(
            initialDestinations,
            assetKey,
            initialConditions);
    }

    public string AssetKey { get; }
    public AssetSource? Asset { get; }
    public bool IsAdopted { get; set; }
    public IReadOnlyList<MusicSettingKey> DestinationKeys => _destinationKeys;
    public IReadOnlyList<MusicGenerationTrackPlan> Tracks => _tracks;
    public IReadOnlyList<MusicConditionSource> Conditions => _tracks
        .SelectMany(track => track.Conditions)
        .DistinctBy(MusicConditionFormatter.CreateKey, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public void ReplaceDestinations(IEnumerable<MusicSettingSource> destinations)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        _destinationKeys = CreateKeys(destinations);
    }

    public void ReplaceConditions(IEnumerable<MusicConditionSource> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        var normalized = CreateConditions(conditions);
        foreach (var track in _tracks)
        {
            track.ReplaceConditions(normalized);
        }
    }

    /// <summary>
    /// Applies the pre-Track-schema condition state only when the entry has one
    /// Track. An old draft or manifest cannot identify which Track was edited,
    /// so applying its aggregate conditions to multiple Tracks would overwrite
    /// their individually parsed conditions.
    /// </summary>
    public bool TryReplaceLegacyConditions(
        IEnumerable<MusicConditionSource> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        if (_tracks.Count != 1)
        {
            return false;
        }

        ReplaceConditions(conditions);
        return true;
    }

    public IReadOnlyList<MusicConditionSource>? GetTrackConditions(string trackKey)
    {
        if (string.IsNullOrWhiteSpace(trackKey))
        {
            return null;
        }

        return _tracks.FirstOrDefault(track =>
            track.TrackKey.Equals(trackKey, StringComparison.OrdinalIgnoreCase))?.Conditions;
    }

    public void ApplyTrackConditions(IEnumerable<MusicGenerationTrackPlan> trackPlans)
    {
        ArgumentNullException.ThrowIfNull(trackPlans);
        var incoming = trackPlans
            .GroupBy(track => track.TrackKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
        if (incoming.Length == 0)
        {
            return;
        }

        var incomingByKey = incoming.ToDictionary(
            track => track.TrackKey,
            StringComparer.OrdinalIgnoreCase);
        var merged = new List<MusicGenerationTrackPlan>(_tracks.Count + incoming.Length);
        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in _tracks)
        {
            if (incomingByKey.TryGetValue(existing.TrackKey, out var replacement))
            {
                existing.ReplaceConditions(replacement.Conditions);
            }

            merged.Add(existing);
            existingKeys.Add(existing.TrackKey);
        }

        foreach (var replacement in incoming)
        {
            if (!existingKeys.Contains(replacement.TrackKey))
            {
                merged.Add(new MusicGenerationTrackPlan(
                    replacement.TrackKey,
                    replacement.Conditions));
            }
        }

        _tracks = merged;
    }

    public void ReplaceTrackPlans(IEnumerable<MusicGenerationTrackPlan> trackPlans)
    {
        ArgumentNullException.ThrowIfNull(trackPlans);
        var normalized = trackPlans
            .GroupBy(track => track.TrackKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MusicGenerationTrackPlan(
                group.Key,
                group.Last().Conditions))
            .ToArray();
        _tracks = normalized;
    }

    public static string CreateAssetKey(AssetSource asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return string.Join(
            "\u001f",
            new[]
            {
                asset.ModPath,
                asset.ModName,
                asset.SourceKind.ToString(),
                asset.VirtualPath,
                asset.SourcePath,
                asset.ArchiveEntryPath ?? string.Empty
            });
    }

    private static IReadOnlyList<MusicSettingKey> CreateKeys(
        IEnumerable<MusicSettingSource> destinations) =>
        destinations
            .Select(MusicSettingKey.From)
            .Distinct()
            .ToArray();

    private static IReadOnlyList<MusicSettingSource> MaterializeDestinations(
        IEnumerable<MusicSettingSource> destinations)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        return destinations.ToArray();
    }

    private static IReadOnlyList<MusicConditionSource> CreateConditions(
        IEnumerable<MusicConditionSource>? conditions) =>
        (conditions ?? Array.Empty<MusicConditionSource>())
            .DistinctBy(MusicConditionFormatter.CreateKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public enum MusicGenerationPlanConflictKind
{
    DuplicateVirtualPath,
    MultipleGeneratedMusicTypesForRecord
}

public sealed record MusicGenerationPlanConflict(
    MusicGenerationPlanConflictKind Kind,
    string Subject,
    string Message,
    IReadOnlyList<MusicGenerationPlanEntry> Entries,
    MusicSettingScope? TargetScope = null,
    string? TargetFormKey = null);

public sealed record MusicSettingKey(
    MusicSettingScope Scope,
    string ScopeFormKey,
    string MusicTypeFormKey)
{
    public static MusicSettingKey From(MusicSettingSource setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return new MusicSettingKey(
            setting.Scope,
            setting.ScopeFormKey,
            setting.MusicTypeFormKey);
    }
}
