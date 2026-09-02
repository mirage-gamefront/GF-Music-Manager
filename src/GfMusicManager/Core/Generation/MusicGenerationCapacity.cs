using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;

namespace GfMusicManager.Core.Generation;

public sealed record MusicGenerationCapacityPolicy(
    int MaxNewRecordsPerPlugin,
    bool AllowSplit = true)
{
    public static MusicGenerationCapacityPolicy CurrentAe { get; } = new(4096);

    public static MusicGenerationCapacityPolicy Legacy { get; } = new(2048);
}

public sealed record MusicGenerationPluginEstimate(
    string PluginFileName,
    int NewMusicTrackRecordCount,
    int NewMusicTypeRecordCount,
    IReadOnlyList<string> AssetKeys)
{
    public int NewRecordCount => NewMusicTrackRecordCount + NewMusicTypeRecordCount;
}

public sealed record MusicGenerationCapacityEstimate(
    int AdoptedAssetCount,
    int MissingAssetEntryCount,
    int NewMusicTrackRecordCount,
    int NewMusicTypeRecordCount,
    int WorldSpaceOverrideCount,
    int MaxNewRecordsPerPlugin,
    IReadOnlyList<MusicGenerationPluginEstimate> Plugins,
    bool AllowSplit)
{
    public int NewRecordCount => NewMusicTrackRecordCount + NewMusicTypeRecordCount;

    public bool RequiresSplit => Plugins.Count > 1;

    public bool IsBlockedByCapacity => RequiresSplit && !AllowSplit;

    public bool HasAdoptedAssets => AdoptedAssetCount > 0;

    public bool IsValid => HasAdoptedAssets &&
                           MissingAssetEntryCount == 0 &&
                           !IsBlockedByCapacity &&
                           Plugins.All(plugin => plugin.NewRecordCount <= MaxNewRecordsPerPlugin);
}

public sealed class MusicGenerationCapacityPlanner
{
    public MusicGenerationCapacityEstimate Estimate(
        MusicGenerationPlan plan,
        MusicGenerationCapacityPolicy policy,
        bool worldSpaceIndividualAssignment = false,
        IReadOnlySet<string>? selectedWorldSpaceFormKeys = null,
        int additionalMusicTypeRecordCount = 0,
        IReadOnlySet<string>? staticTrackAssetKeys = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxNewRecordsPerPlugin <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                policy.MaxNewRecordsPerPlugin,
                "The maximum number of new records per plugin must be greater than zero.");
        }
        if (additionalMusicTypeRecordCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(additionalMusicTypeRecordCount),
                additionalMusicTypeRecordCount,
                "The additional Music Type record count cannot be negative.");
        }

        var adoptedEntries = plan.Entries
            .Where(entry => entry.IsAdopted)
            .ToArray();
        var adoptedAssets = adoptedEntries
            .Where(entry => entry.Asset is not null)
            .ToArray();
        var missingAssetEntryCount = adoptedEntries.Length - adoptedAssets.Length;
        var recordAssets = staticTrackAssetKeys is null
            ? adoptedAssets
            : adoptedAssets
                .Where(entry => staticTrackAssetKeys.Contains(entry.AssetKey))
                .ToArray();
        var pluginEstimates = BuildPluginEstimates(
            recordAssets,
            policy.MaxNewRecordsPerPlugin,
            CountDedicatedWorldSpaceMusicTypes(
                adoptedEntries,
                worldSpaceIndividualAssignment,
                selectedWorldSpaceFormKeys) +
            additionalMusicTypeRecordCount);
        var newMusicTypeRecordCount = pluginEstimates.Sum(
            plugin => plugin.NewMusicTypeRecordCount);
        var newMusicTrackRecordCount = pluginEstimates.Sum(
            plugin => plugin.NewMusicTrackRecordCount);
        var worldSpaceOverrideCount = adoptedAssets
            .SelectMany(entry => entry.DestinationKeys)
            .Where(destination => destination.Scope == MusicSettingScope.WorldSpace)
            .Select(destination => destination.ScopeFormKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return new MusicGenerationCapacityEstimate(
            adoptedAssets.Length,
            missingAssetEntryCount,
            newMusicTrackRecordCount,
            newMusicTypeRecordCount,
            worldSpaceOverrideCount,
            policy.MaxNewRecordsPerPlugin,
            pluginEstimates,
            policy.AllowSplit);
    }

    private static IReadOnlyList<MusicGenerationPluginEstimate> BuildPluginEstimates(
        IReadOnlyList<MusicGenerationPlanEntry> adoptedAssets,
        int maxNewRecordsPerPlugin,
        int newMusicTypeRecordCount)
    {
        if (adoptedAssets.Count == 0)
        {
            if (newMusicTypeRecordCount == 0)
            {
                return Array.Empty<MusicGenerationPluginEstimate>();
            }

            if (newMusicTypeRecordCount >= maxNewRecordsPerPlugin)
            {
                throw new InvalidOperationException(
                    "The generated Music Type records alone exceed the plugin capacity.");
            }

            return new[]
            {
                new MusicGenerationPluginEstimate(
                    GetPluginFileName(1),
                    0,
                    newMusicTypeRecordCount,
                    Array.Empty<string>())
            };
        }

        if (newMusicTypeRecordCount >= maxNewRecordsPerPlugin)
        {
            throw new InvalidOperationException(
                "The generated Music Type records alone exceed the plugin capacity.");
        }

        var estimates = new List<MusicGenerationPluginEstimate>();
        var firstPluginTrackCapacity = maxNewRecordsPerPlugin - newMusicTypeRecordCount;
        var offset = 0;
        var isFirstPlugin = true;
        while (offset < adoptedAssets.Count)
        {
            var capacity = isFirstPlugin ? firstPluginTrackCapacity : maxNewRecordsPerPlugin;
            var batch = new List<MusicGenerationPlanEntry>();
            var trackCount = 0;
            while (offset + batch.Count < adoptedAssets.Count)
            {
                var candidate = adoptedAssets[offset + batch.Count];
                var candidateTrackCount = Math.Max(candidate.Tracks.Count, 1);
                if (batch.Count > 0 && trackCount + candidateTrackCount > capacity)
                {
                    break;
                }

                batch.Add(candidate);
                trackCount += candidateTrackCount;
                if (trackCount >= capacity)
                {
                    break;
                }
            }

            if (batch.Count == 0)
            {
                // Keep the oversized asset visible in the estimate so the caller
                // can report a capacity error instead of silently dropping it.
                var oversized = adoptedAssets[offset];
                batch.Add(oversized);
                trackCount = Math.Max(oversized.Tracks.Count, 1);
            }
            var index = estimates.Count + 1;
            estimates.Add(new MusicGenerationPluginEstimate(
                GetPluginFileName(index),
                trackCount,
                isFirstPlugin ? newMusicTypeRecordCount : 0,
                batch.Select(entry => entry.AssetKey).ToArray()));
            offset += batch.Count;
            isFirstPlugin = false;
        }

        return estimates;
    }

    public static string GetPluginFileName(int index)
    {
        if (index < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Plugin index must be positive.");
        }

        return index == 1
            ? "GF Music Product.esp"
            : $"GF Music Product - {index:00}.esp";
    }

    private static int CountDedicatedWorldSpaceMusicTypes(
        IReadOnlyList<MusicGenerationPlanEntry> adoptedEntries,
        bool enabled,
        IReadOnlySet<string>? selectedWorldSpaceFormKeys)
    {
        if (!enabled)
        {
            return 0;
        }

        var keys = selectedWorldSpaceFormKeys is { Count: > 0 }
            ? selectedWorldSpaceFormKeys
            : adoptedEntries
                .SelectMany(entry => entry.DestinationKeys)
                .Where(destination => destination.Scope == MusicSettingScope.WorldSpace)
                .Select(destination => destination.ScopeFormKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return adoptedEntries
            .SelectMany(entry => entry.DestinationKeys)
            .Where(destination => destination.Scope == MusicSettingScope.WorldSpace)
            .Where(destination => keys.Contains(destination.ScopeFormKey))
            .Select(destination => destination.ScopeFormKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }
}
