using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// Resolves which adopted assets need static Music Track records for the
/// generated ESP destination plan.
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
