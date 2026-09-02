using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Application;

/// <summary>
/// Creates the user-owned generation plan from a read-only scan result.
/// Adoption starts as true, matching the desktop behavior; later user decisions
/// are applied to the returned Core plan without requiring WPF types.
/// </summary>
public sealed class MusicPlanApplicationService
{
    public MusicPlanSnapshot Edit(
        MusicPlanSnapshot snapshot,
        IReadOnlyList<string> adoptAssetKeys,
        IReadOnlyList<string> excludeAssetKeys,
        bool? keepVanillaMusic = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(adoptAssetKeys);
        ArgumentNullException.ThrowIfNull(excludeAssetKeys);

        var adoptKeys = adoptAssetKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludeKeys = excludeAssetKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateKeys = adoptKeys.Intersect(excludeKeys, StringComparer.OrdinalIgnoreCase).ToArray();
        if (duplicateKeys.Length > 0)
        {
            throw new ArgumentException(
                $"同じ音源を採用と除外の両方に指定しています：{string.Join(", ", duplicateKeys)}");
        }

        var entries = snapshot.Entries ?? Array.Empty<MusicPlanEntrySnapshot>();
        var knownKeys = entries
            .Select(entry => entry.AssetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknownKeys = adoptKeys
            .Concat(excludeKeys)
            .Where(key => !knownKeys.Contains(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknownKeys.Length > 0)
        {
            throw new ArgumentException(
                $"計画に存在しない音源キーです：{string.Join(", ", unknownKeys)}");
        }

        return snapshot with
        {
            KeepVanillaMusic = keepVanillaMusic ?? snapshot.KeepVanillaMusic,
            Entries = entries
                .Select(entry => adoptKeys.Contains(entry.AssetKey)
                    ? entry with { IsAdopted = true }
                    : excludeKeys.Contains(entry.AssetKey)
                        ? entry with { IsAdopted = false }
                        : entry)
                .ToArray()
        };
    }

    public MusicGenerationPlan CreatePlan(
        MusicScanApplicationResult scanResult,
        IProgress<MusicGenerationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(scanResult);

        return PreparePlan(scanResult, progress).Plan;
    }

    public MusicGenerationPlan CreatePlan(
        MusicScanApplicationResult scanResult,
        Action<int, int> progress)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        ArgumentNullException.ThrowIfNull(progress);

        progress(0, scanResult.Scan.Assets.Count);
        return PreparePlan(
            scanResult,
            new CallbackProgress<MusicGenerationProgress>(item =>
                progress(item.Current, item.Total))).Plan;
    }

    public MusicPlanPreparationResult PreparePlan(
        MusicScanApplicationResult scanResult,
        IProgress<MusicGenerationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(scanResult);

        var bindings = MusicAssetBindingIndex.Create(scanResult.MusicAnalysis);
        var plan = CreatePlan(scanResult.Scan.Assets, bindings, progress);
        return new MusicPlanPreparationResult(plan, bindings);
    }

    public int ApplyDefaultPathConflictSelection(
        MusicGenerationPlan plan,
        AudioDuplicateAnalysisResult duplicates,
        IReadOnlyList<ModSource> mods,
        IReadOnlySet<string>? excludedAssetKeys = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(duplicates);
        ArgumentNullException.ThrowIfNull(mods);

        var priorities = mods.ToDictionary(
            mod => mod.Name,
            mod => mod.Priority,
            StringComparer.OrdinalIgnoreCase);
        var winners = AudioDuplicateDefaultSelection.SelectPathConflictWinners(
            duplicates.Groups,
            priorities);
        var changed = 0;
        foreach (var entry in plan.Entries)
        {
            if (excludedAssetKeys?.Contains(entry.AssetKey) == true)
            {
                continue;
            }

            if (entry.Asset is null || !duplicates.Groups.Any(group =>
                    group.Kind == AudioDuplicateKind.PathConflict &&
                    group.Sources.Any(source =>
                        source.AssetKey.Equals(entry.AssetKey, StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            var shouldAdopt = winners.Contains(entry.AssetKey);
            if (entry.IsAdopted != shouldAdopt)
            {
                entry.IsAdopted = shouldAdopt;
                changed++;
            }
        }

        return changed;
    }

    public MusicGenerationPlan CreatePlan(
        IReadOnlyList<AssetSource> assets,
        MusicAnalysisResult musicAnalysis,
        IProgress<MusicGenerationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(musicAnalysis);

        return CreatePlan(
            assets,
            MusicAssetBindingIndex.Create(musicAnalysis),
            progress);
    }

    private static MusicGenerationPlan CreatePlan(
        IReadOnlyList<AssetSource> assets,
        MusicAssetBindingIndex bindings,
        IProgress<MusicGenerationProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(bindings);

        var plan = new MusicGenerationPlan();
        var orderedAssets = assets
            .OrderBy(item => item.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var total = orderedAssets.Length;
        for (var index = 0; index < total; index++)
        {
            var asset = orderedAssets[index];
            var binding = bindings.Get(asset.VirtualPath);
            var assetKey = MusicGenerationPlanEntry.CreateAssetKey(asset);
            plan.GetOrCreatePrepared(
                asset,
                binding.Settings,
                binding.CreateTrackPlans(assetKey));
            if (progress is not null &&
                (index == 0 || (index + 1) % 25 == 0 || index + 1 == total))
            {
                var percent = total == 0
                    ? 10
                    : 5 + (index + 1) * 5d / total;
                progress.Report(new MusicGenerationProgress(
                    MusicGenerationProgressStage.Preparing,
                    UiText.Get("Progress.ResultPlan"),
                    percent,
                    index + 1,
                    total));
            }
        }

        return plan;
    }

    public MusicPlanSnapshot Capture(MusicGenerationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return new MusicPlanSnapshot(
            plan.KeepVanillaMusic,
            plan.Entries
                .Select(entry => new MusicPlanEntrySnapshot(
                    entry.AssetKey,
                    entry.IsAdopted,
                    entry.DestinationKeys.ToArray(),
                    entry.Tracks
                        .Select(track => new MusicPlanTrackSnapshot(
                            track.TrackKey,
                            track.Conditions.ToArray()))
                        .ToArray(),
                    entry.Conditions.ToArray()))
                .ToArray());
    }

    public MusicPlanApplyResult Apply(
        MusicGenerationPlan plan,
        MusicPlanSnapshot snapshot,
        IReadOnlyList<MusicSettingSource> availableSettings)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(availableSettings);

        plan.KeepVanillaMusic = snapshot.KeepVanillaMusic;
        var entriesByAssetKey = plan.Entries.ToDictionary(
            entry => entry.AssetKey,
            StringComparer.OrdinalIgnoreCase);
        var settingsByKey = availableSettings
            .GroupBy(CreateSettingKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);
        var missingAssetKeys = new List<string>();
        var restoredEntryCount = 0;

        foreach (var savedEntry in snapshot.Entries ?? Array.Empty<MusicPlanEntrySnapshot>())
        {
            if (!entriesByAssetKey.TryGetValue(savedEntry.AssetKey, out var entry))
            {
                missingAssetKeys.Add(savedEntry.AssetKey);
                continue;
            }

            entry.IsAdopted = savedEntry.IsAdopted;
            var destinationKeys = savedEntry.DestinationKeys ?? Array.Empty<MusicSettingKey>();
            var destinations = destinationKeys
                .Select(key => settingsByKey.TryGetValue(
                    CreateSettingKey(key),
                    out var setting)
                    ? setting
                    : null)
                .Where(setting => setting is not null)
                .Cast<MusicSettingSource>()
                .ToArray();
            if (destinationKeys.Count == 0 || destinations.Length > 0)
            {
                entry.ReplaceDestinations(destinations);
            }

            var savedTracks = savedEntry.Tracks ?? Array.Empty<MusicPlanTrackSnapshot>();
            if (savedTracks.Count > 0)
            {
                entry.ReplaceTrackPlans(savedTracks.Select(track =>
                    new MusicGenerationTrackPlan(track.TrackKey, track.Conditions)));
            }
            else if (savedEntry.LegacyConditions is { Count: > 0 } &&
                     entry.Tracks.Count == 1)
            {
                entry.TryReplaceLegacyConditions(savedEntry.LegacyConditions);
            }

            restoredEntryCount++;
        }

        return new MusicPlanApplyResult(
            restoredEntryCount,
            missingAssetKeys.Count,
            missingAssetKeys);
    }

    private static string CreateSettingKey(MusicSettingSource setting) =>
        CreateSettingKey(MusicSettingKey.From(setting));

    private static string CreateSettingKey(MusicSettingKey setting) => string.Join(
        "\u001f",
        setting.Scope,
        setting.ScopeFormKey,
        setting.MusicTypeFormKey);

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
