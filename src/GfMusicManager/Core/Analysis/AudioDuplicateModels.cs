using GfMusicManager.Core.Planning;
using GfMusicManager.Core.Localization;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Analysis;

public enum AudioDuplicateKind
{
    PathConflict,
    ContentMatch,
    SimilarCandidate
}

public sealed record AudioDuplicateSource(
    AssetSource Asset,
    string ContentHash,
    double? DurationSeconds = null)
{
    public string AssetKey => MusicGenerationPlanEntry.CreateAssetKey(Asset);

    public string SourceKindText => Asset.IsFromArchive
        ? "BSA"
        : UiText.Get("Analysis.AudioDuplicate.SourceKind.Loose");

    public string LocationText => Asset.IsFromArchive
        ? $"{Asset.SourcePath}!{Asset.ArchiveEntryPath ?? Asset.VirtualPath}"
        : Asset.SourcePath;

    public string WinnerText => Asset.IsVfsWinner && Asset.ModEnabled
        ? UiText.Get("Analysis.AudioDuplicate.Winner.Current")
        : Asset.ModEnabled
            ? UiText.Get("Analysis.AudioDuplicate.Winner.EnabledNonWinner")
            : UiText.Get("Analysis.AudioDuplicate.Winner.DisabledMod");
}

public sealed record AudioDuplicateGroup(
    string GroupId,
    AudioDuplicateKind Kind,
    string Subject,
    string Explanation,
    string DetectionMethod,
    IReadOnlyList<AudioDuplicateSource> Sources,
    double? SimilarityScore = null)
{
    public string KindText => Kind switch
    {
        AudioDuplicateKind.PathConflict => UiText.Get("Analysis.AudioDuplicate.Kind.PathConflict"),
        AudioDuplicateKind.ContentMatch => UiText.Get("Analysis.AudioDuplicate.Kind.ContentMatch"),
        AudioDuplicateKind.SimilarCandidate => UiText.Get("Analysis.AudioDuplicate.Kind.SimilarCandidate"),
        _ => UiText.Get("Analysis.AudioDuplicate.Kind.Unknown")
    };

    public bool RequiresSingleSelection => Kind == AudioDuplicateKind.PathConflict;

    public string ScoreText => SimilarityScore is null
        ? string.Empty
        : UiText.Format("Analysis.AudioDuplicate.Score", SimilarityScore.Value);
}

public sealed record AudioDuplicateAnalysisResult(
    IReadOnlyList<AudioDuplicateGroup> Groups,
    int AnalyzedSourceCount,
    int ReadFailureCount,
    int SimilarityComparisonCount,
    bool SimilarityDecoderAvailable)
{
    public int PathConflictCount => Groups.Count(group => group.Kind == AudioDuplicateKind.PathConflict);
    public int ContentMatchCount => Groups.Count(group => group.Kind == AudioDuplicateKind.ContentMatch);
    public int SimilarCandidateCount => Groups.Count(group => group.Kind == AudioDuplicateKind.SimilarCandidate);
}

public sealed record AudioDuplicateReviewDecision(
    AudioDuplicateGroup Group,
    IReadOnlySet<string> AdoptedAssetKeys);

public static class AudioDuplicateDefaultSelection
{
    public static IReadOnlySet<string> SelectPathConflictWinners(
        IEnumerable<AudioDuplicateGroup> groups,
        IReadOnlyDictionary<string, int>? modPriorities = null)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var priorities = modPriorities ?? EmptyPriorities;
        return groups
            .Where(group => group.Kind == AudioDuplicateKind.PathConflict)
            .SelectMany(group => group.Sources
                .OrderByDescending(source => source.Asset.IsVfsWinner)
                .ThenByDescending(source => source.Asset.ModEnabled)
                .ThenByDescending(source => GetModPriority(priorities, source.Asset.ModName))
                .ThenBy(source => source.Asset.SourceKind == AssetSourceKind.Loose ? 0 : 1)
                .ThenBy(source => source.Asset.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Take(1)
                .Select(source => source.AssetKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyPriorities =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static int GetModPriority(
        IReadOnlyDictionary<string, int> priorities,
        string modName) =>
        priorities.TryGetValue(modName, out var priority) ? priority : int.MinValue;
}
