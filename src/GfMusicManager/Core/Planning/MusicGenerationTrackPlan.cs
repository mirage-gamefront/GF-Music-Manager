using GfMusicManager.Core.Analysis;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Planning;

/// <summary>
/// One generated Music Track decision for a scanned audio source.
/// Conditions belong to this Track, not to the audio source as a whole.
/// </summary>
public sealed class MusicGenerationTrackPlan
{
    private IReadOnlyList<MusicConditionSource> _conditions;

    public MusicGenerationTrackPlan(
        string trackKey,
        IEnumerable<MusicConditionSource>? conditions = null,
        string? finaleAudioPath = null,
        AssetSource? finaleAsset = null)
    {
        if (string.IsNullOrWhiteSpace(trackKey))
        {
            throw new ArgumentException("Music Trackキーが空です。", nameof(trackKey));
        }

        TrackKey = trackKey;
        _conditions = CreateConditions(conditions);
        FinaleAudioPath = string.IsNullOrWhiteSpace(finaleAudioPath)
            ? null
            : MusicAudioPathIdentity.NormalizeAssetPath(finaleAudioPath);
        FinaleAsset = finaleAsset;
    }

    public string TrackKey { get; }

    public IReadOnlyList<MusicConditionSource> Conditions => _conditions;

    public string? FinaleAudioPath { get; }

    public AssetSource? FinaleAsset { get; }

    public void ReplaceConditions(IEnumerable<MusicConditionSource> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        _conditions = CreateConditions(conditions);
    }

    private static IReadOnlyList<MusicConditionSource> CreateConditions(
        IEnumerable<MusicConditionSource>? conditions) =>
        (conditions ?? Array.Empty<MusicConditionSource>())
            .DistinctBy(MusicConditionFormatter.CreateKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

/// <summary>
/// Uses the same identity for the detail window and generation plan so a
/// condition edited for one visible Track is restored and generated for that
/// same Track.
/// </summary>
public static class MusicGenerationTrackKey
{
    public static string CreateDefinitionIdentity(MusicTrackSource track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return string.Join("\u001f", track.FormKey, track.Record.Plugin.Path);
    }

    public static string Create(MusicTrackSource track)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (string.IsNullOrWhiteSpace(track.EditorId))
        {
            return string.Join("\u001f", "FormKey", track.FormKey, track.Record.Plugin.Path);
        }

        var audioPaths = track.MatchingAudioPaths
            .Concat(track.MatchingFinaleAudioPaths)
            .Order(StringComparer.OrdinalIgnoreCase);
        var conditions = track.Conditions
            .Select(MusicConditionFormatter.CreateKey)
            .Order(StringComparer.OrdinalIgnoreCase);
        return string.Join(
            "\u001f",
            "EditorID",
            track.EditorId,
            string.Join("\u001e", audioPaths),
            string.Join("\u001e", conditions));
    }

    public static string CreateFallback(string assetKey) =>
        string.Join("\u001f", "Asset", assetKey);
}

public static class MusicGenerationTrackPlanFactory
{
    public static MusicGenerationTrackPlan Create(
        MusicTrackSource track,
        IReadOnlyList<AssetSource>? assets = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        var finaleCandidates = track.ResolvedFinaleAudioPaths
            .Concat(track.FinaleAudioPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var finaleAsset = finaleCandidates
            .SelectMany(path => (assets ?? Array.Empty<AssetSource>())
                .Where(asset => string.Equals(
                    MusicAudioPathIdentity.NormalizeMusicIdentity(asset.VirtualPath),
                    MusicAudioPathIdentity.NormalizeMusicIdentity(path),
                    StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(asset => asset.ModName.Equals(
                track.Record.Plugin.ModName,
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset => asset.IsVfsWinner && asset.ModEnabled)
            .FirstOrDefault();
        var finalePath = finaleAsset?.VirtualPath ?? finaleCandidates.FirstOrDefault();
        return new MusicGenerationTrackPlan(
            MusicGenerationTrackKey.Create(track),
            track.Conditions,
            finalePath,
            finaleAsset);
    }

    public static IReadOnlyList<MusicGenerationTrackPlan> Create(
        AssetSource asset,
        IEnumerable<MusicSettingSource> settings,
        IEnumerable<MusicConditionSource>? fallbackConditions = null)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(settings);

        var tracks = settings
            .SelectMany(setting => setting.Tracks)
            .Where(track => track.MatchesAudioPath(asset.VirtualPath))
            .GroupBy(MusicGenerationTrackKey.Create, StringComparer.OrdinalIgnoreCase)
            .Select(group => Create(group.First()))
            .OrderBy(track => track.TrackKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return tracks.Length > 0
            ? tracks
            : new[]
            {
                new MusicGenerationTrackPlan(
                    MusicGenerationTrackKey.CreateFallback(
                        MusicGenerationPlanEntry.CreateAssetKey(asset)),
                    fallbackConditions)
            };
    }

    public static IReadOnlyList<MusicGenerationTrackPlan> Create(
        IEnumerable<MusicSettingSource> settings,
        string assetKey,
        IEnumerable<MusicConditionSource>? fallbackConditions = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(assetKey))
        {
            throw new ArgumentException("音源キーが空です。", nameof(assetKey));
        }

        var tracks = settings
            .SelectMany(setting => setting.Tracks)
            .GroupBy(MusicGenerationTrackKey.Create, StringComparer.OrdinalIgnoreCase)
            .Select(group => Create(group.First()))
            .OrderBy(track => track.TrackKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return tracks.Length > 0
            ? tracks
            : new[]
            {
                new MusicGenerationTrackPlan(
                    MusicGenerationTrackKey.CreateFallback(assetKey),
                    fallbackConditions)
            };
    }
}
