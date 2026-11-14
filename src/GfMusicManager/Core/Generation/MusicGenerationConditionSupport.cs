using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// One Track that was left out of generation because a playback condition
/// cannot be written to the generated Music Track.
/// </summary>
public sealed record MusicGenerationSkippedTrack(
    string AssetKey,
    string ModName,
    string VirtualPath,
    string TrackKey,
    string Message);

/// <summary>
/// The plan that generation will actually use, together with the Tracks that
/// were removed from it.  The user's own plan is never modified.
/// </summary>
public sealed record MusicGenerationConditionSupportResult(
    MusicGenerationPlan Plan,
    IReadOnlyList<MusicGenerationSkippedTrack> SkippedTracks)
{
    public static MusicGenerationConditionSupportResult Empty(MusicGenerationPlan plan) =>
        new(plan, Array.Empty<MusicGenerationSkippedTrack>());

    public IReadOnlySet<string> SkippedAssetKeys => SkippedTracks
        .Select(track => track.AssetKey)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> SkippedModNames => SkippedTracks
        .Select(track => track.ModName)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Separates Tracks whose playback conditions can be written from those that
/// cannot, so one unsupported condition does not abort the whole generation.
/// A Track is never rewritten into a different condition: it is either carried
/// over with its conditions intact or left out and reported.
/// </summary>
public static class MusicGenerationConditionSupport
{
    /// <summary>
    /// Returns why the condition cannot be written, or null when it can.  The
    /// check is the same one the output writer applies before writing.
    /// </summary>
    public static string? GetUnsupportedReason(MusicConditionSource condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        try
        {
            MusicGenerationOutputWriter.ValidateCondition(condition);
            return null;
        }
        catch (MusicGenerationOutputException exception)
        {
            return exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return UiText.Format(
                "Generation.Output.ConditionValidationFailed",
                GenerationMessageFormatter.ExceptionDetail(exception));
        }
    }

    public static MusicGenerationConditionSupportResult Filter(MusicGenerationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var skipped = new List<MusicGenerationSkippedTrack>();
        var entries = new List<MusicGenerationPlanEntry>(plan.Entries.Count);
        foreach (var entry in plan.Entries)
        {
            if (!entry.IsAdopted || entry.Asset is null)
            {
                entries.Add(entry);
                continue;
            }

            var kept = new List<MusicGenerationTrackPlan>(entry.Tracks.Count);
            var skippedBefore = skipped.Count;
            foreach (var track in entry.Tracks)
            {
                var reason = track.Conditions
                    .Select(GetUnsupportedReason)
                    .FirstOrDefault(value => value is not null);
                if (reason is null)
                {
                    kept.Add(track);
                    continue;
                }

                skipped.Add(new MusicGenerationSkippedTrack(
                    entry.AssetKey,
                    entry.Asset.ModName,
                    entry.Asset.VirtualPath,
                    track.TrackKey,
                    MusicGenerationFailureContext.Append(
                        reason,
                        entry.Asset,
                        track.TrackKey)));
            }

            entries.Add(skipped.Count == skippedBefore
                ? entry
                : entry.CloneWithTracks(isAdopted: kept.Count > 0, kept));
        }

        return skipped.Count == 0
            ? MusicGenerationConditionSupportResult.Empty(plan)
            : new MusicGenerationConditionSupportResult(
                plan.CreateDerivedPlan(entries),
                skipped);
    }
}
