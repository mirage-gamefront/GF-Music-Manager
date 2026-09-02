using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// The MTD key is a Music Type EditorID, not a full FormKey.  Multiple source
/// Music Type records can therefore resolve to the same MTD entry.  This
/// aggregate keeps the complete Track list for that one EditorID.
/// </summary>
internal sealed record MusicTypeMtdAggregate(
    string MusicTypeEditorId,
    IReadOnlyList<MusicGenerationMusicTypeTrackSet> SourceMusicTypes)
{
    public IReadOnlyList<MusicTrackSource> OfficialTracks => SourceMusicTypes
        .SelectMany(musicType => musicType.OfficialTracks)
        .DistinctBy(track => track.FormKey, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public IReadOnlyList<string> OfficialTrackFormKeys => OfficialTracks
        .Select(track => track.FormKey)
        .ToArray();

    public IReadOnlyList<MusicGenerationPlanEntry> GeneratedEntries => SourceMusicTypes
        .SelectMany(musicType => musicType.GeneratedEntries)
        .DistinctBy(entry => entry.AssetKey, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

internal sealed record MusicTypeMtdAggregationResult(
    IReadOnlyList<MusicTypeMtdAggregate> Aggregates,
    IReadOnlyList<string> MissingDefinitionFormKeys,
    IReadOnlyList<string> MissingEditorIdFormKeys);

/// <summary>
/// Creates the single EditorID-keyed view shared by MTD output and
/// post-generation diagnostics.
/// </summary>
internal static class MusicTypeMtdAggregator
{
    public static MusicTypeMtdAggregationResult Build(
        MusicGenerationPlanResolution planResolution,
        IReadOnlyList<MusicGenerationPlanEntry> adoptedEntries,
        IReadOnlyList<MusicSettingSource> settings,
        bool worldSpaceIndividualAssignment,
        IReadOnlySet<string> selectedWorldSpaceFormKeys)
    {
        ArgumentNullException.ThrowIfNull(planResolution);
        ArgumentNullException.ThrowIfNull(adoptedEntries);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(selectedWorldSpaceFormKeys);

        // Cell itself is assigned by SkyPatcher, but the Music Type referenced
        // by that Cell still needs its final Track list in MTD.
        var mtdMusicTypeFormKeys = adoptedEntries
            .SelectMany(entry => entry.DestinationKeys)
            .Where(destination => !IsIntegratedDestination(
                planResolution,
                destination,
                worldSpaceIndividualAssignment,
                selectedWorldSpaceFormKeys))
            .Select(destination => destination.MusicTypeFormKey)
            .Where(formKey => !string.IsNullOrWhiteSpace(formKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var settingsByMusicType = settings
            .GroupBy(setting => setting.MusicTypeFormKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<MusicSettingSource>)group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var groupedMusicTypes = new Dictionary<
            string,
            List<MusicGenerationMusicTypeTrackSet>>(
            StringComparer.OrdinalIgnoreCase);
        var missingDefinitionFormKeys = new List<string>();
        var missingEditorIdFormKeys = new List<string>();

        foreach (var musicType in planResolution.MusicTypes.Where(musicType =>
                     mtdMusicTypeFormKeys.Contains(musicType.MusicTypeFormKey)))
        {
            if (!settingsByMusicType.TryGetValue(
                    musicType.MusicTypeFormKey,
                    out var applicableSettings) ||
                applicableSettings.Count == 0)
            {
                missingDefinitionFormKeys.Add(musicType.MusicTypeFormKey);
                continue;
            }

            var representative = SelectRepresentativeSetting(applicableSettings);
            if (string.IsNullOrWhiteSpace(representative.MusicTypeEditorId))
            {
                missingEditorIdFormKeys.Add(musicType.MusicTypeFormKey);
                continue;
            }

            if (!groupedMusicTypes.TryGetValue(
                    representative.MusicTypeEditorId,
                    out var sourceMusicTypes))
            {
                sourceMusicTypes = new List<MusicGenerationMusicTypeTrackSet>();
                groupedMusicTypes.Add(
                    representative.MusicTypeEditorId,
                    sourceMusicTypes);
            }

            var generatedEntries = musicType.GeneratedEntries
                .Where(entry => entry.DestinationKeys.Any(destination =>
                    destination.MusicTypeFormKey.Equals(
                        musicType.MusicTypeFormKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    !IsIntegratedDestination(
                        planResolution,
                        destination,
                        worldSpaceIndividualAssignment,
                        selectedWorldSpaceFormKeys)))
                .ToArray();
            sourceMusicTypes.Add(musicType with
            {
                GeneratedEntries = generatedEntries
            });
        }

        var aggregates = groupedMusicTypes
            .Select(pair => new MusicTypeMtdAggregate(
                pair.Key,
                pair.Value.ToArray()))
            .ToArray();
        return new MusicTypeMtdAggregationResult(
            aggregates,
            missingDefinitionFormKeys,
            missingEditorIdFormKeys);
    }

    private static bool IsIntegratedDestination(
        MusicGenerationPlanResolution planResolution,
        MusicSettingKey destination,
        bool worldSpaceIndividualAssignment,
        IReadOnlySet<string> selectedWorldSpaceFormKeys)
    {
        if (!planResolution.TryGetIntegrationTarget(
                destination.Scope,
                destination.ScopeFormKey,
                out _))
        {
            return false;
        }

        return destination.Scope != MusicSettingScope.WorldSpace ||
               (worldSpaceIndividualAssignment &&
                selectedWorldSpaceFormKeys.Contains(destination.ScopeFormKey));
    }

    public static MusicSettingSource SelectRepresentativeSetting(
        IEnumerable<MusicSettingSource> candidates) =>
        candidates
            .OrderByDescending(setting => setting.Record.IsWinner)
            .ThenByDescending(setting => setting.Record.Plugin.LoadOrderIndex)
            .ThenByDescending(setting => setting.Record.Plugin.ModPriority)
            .ThenByDescending(setting => setting.MusicTypeRecord.IsWinner)
            .ThenByDescending(setting => setting.MusicTypeRecord.Plugin.LoadOrderIndex)
            .ThenBy(setting => setting.Record.Plugin.Name, StringComparer.OrdinalIgnoreCase)
            .First();
}
