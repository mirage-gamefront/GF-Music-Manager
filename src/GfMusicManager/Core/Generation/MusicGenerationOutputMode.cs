using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// Selects how generated Music Track and Music Type data is materialized.
/// Other output such as audio assets, MTD, Cell SkyPatcher output, and the
/// optional WorldSpace ESP continues through the shared normal pipeline.
/// </summary>
public enum MusicGenerationOutputMode
{
    Normal,
    Dfg
}

/// <summary>
/// Keeps the routing API available for callers that need to inspect the
/// destination plan.  DFG output itself never uses this set for static Track
/// creation; all adopted Tracks are emitted by the DFG package instead.
/// </summary>
public static class MusicGenerationRecordRouting
{
    public static IReadOnlySet<string> GetStaticTrackAssetKeys(
        MusicGenerationPlan plan,
        MusicGenerationPlanResolution resolution,
        bool worldSpaceIndividualAssignment,
        IReadOnlySet<string>? selectedWorldSpaceFormKeys = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(resolution);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in resolution.IntegrationTargets)
        {
            if (target.Scope == MusicSettingScope.WorldSpace &&
                !worldSpaceIndividualAssignment)
            {
                continue;
            }

            if (target.Scope == MusicSettingScope.WorldSpace &&
                selectedWorldSpaceFormKeys is { Count: > 0 } &&
                !selectedWorldSpaceFormKeys.Contains(target.ScopeFormKey))
            {
                continue;
            }

            foreach (var entry in target.GeneratedEntries)
            {
                result.Add(entry.AssetKey);
            }
        }

        if (worldSpaceIndividualAssignment)
        {
            var selectedWorldSpaces = selectedWorldSpaceFormKeys is { Count: > 0 }
                ? selectedWorldSpaceFormKeys
                : plan.Entries
                    .Where(entry => entry.IsAdopted)
                    .SelectMany(entry => entry.DestinationKeys)
                    .Where(destination => destination.Scope == MusicSettingScope.WorldSpace)
                    .Select(destination => destination.ScopeFormKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in plan.Entries.Where(entry => entry.IsAdopted))
            {
                if (entry.DestinationKeys.Any(destination =>
                        destination.Scope == MusicSettingScope.WorldSpace &&
                        selectedWorldSpaces.Contains(destination.ScopeFormKey)))
                {
                    result.Add(entry.AssetKey);
                }
            }
        }

        return result;
    }
}
