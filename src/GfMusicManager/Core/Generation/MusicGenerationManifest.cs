using GfMusicManager.Core.Analysis;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// The persisted edit state for one scanned audio source.  This is separate
/// from GeneratedAssetOutput because excluded sources do not produce an
/// output file, but their adoption state still has to survive a rescan.
/// </summary>
public sealed record MusicGenerationPlanEntryOutput(
    string AssetKey,
    string VirtualPath,
    bool IsAdopted,
    IReadOnlyList<string> DestinationKeys,
    IReadOnlyList<MusicConditionSource> Conditions)
{
    public IReadOnlyList<MusicGenerationTrackPlanOutput> Tracks { get; init; } =
        Array.Empty<MusicGenerationTrackPlanOutput>();
}

public sealed record MusicGenerationTrackPlanOutput(
    string TrackKey,
    IReadOnlyList<MusicConditionSource> Conditions);

/// <summary>
/// The JSON document written beside GF Music Product's generated files.
/// It is an editable-state document, not an audit log.
/// </summary>
public sealed record MusicGenerationManifest(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string OutputModDirectory,
    string MtdFileName,
    bool WorldSpaceIndividualAssignment,
    bool KeepVanillaMusic,
    IReadOnlyList<GeneratedPluginOutput> Plugins,
    IReadOnlyList<GeneratedMusicTrackOutput> Tracks,
    IReadOnlyList<GeneratedWorldSpaceOutput> WorldSpaces,
    IReadOnlyList<GeneratedCellOutput> Cells,
    IReadOnlyList<GeneratedMusicTypeOutput> IntegratedMusicTypes,
    string? CellSkyPatcherFileName,
    IReadOnlyList<GeneratedAssetOutput> Assets,
    IReadOnlyList<MusicGenerationPlanEntryOutput> PlanEntries,
    int NewRecordCount,
    int MaxNewRecordsPerPlugin)
{
    public MusicGenerationOutputMode OutputMode { get; init; } =
        MusicGenerationOutputMode.Normal;

    public string? DfgPackageDirectory { get; init; }
}
