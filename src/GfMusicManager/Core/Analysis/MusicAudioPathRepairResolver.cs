namespace GfMusicManager.Core.Analysis;

internal sealed class MusicAudioPathRepairResolver
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _repairedPathsByIdentity;

    private MusicAudioPathRepairResolver(
        IReadOnlyDictionary<string, IReadOnlyList<string>> repairedPathsByIdentity)
    {
        _repairedPathsByIdentity = repairedPathsByIdentity;
    }

    public static MusicAudioPathRepairResolver Create(
        AdditionalMusicProjectRepairReport additionalMusicProjectRepair,
        FantasySoundtrackProjectRepairReport fantasySoundtrackProjectRepair)
    {
        ArgumentNullException.ThrowIfNull(additionalMusicProjectRepair);
        ArgumentNullException.ThrowIfNull(fantasySoundtrackProjectRepair);

        var repairs = additionalMusicProjectRepair.AudioPathRepairs
            .Select(repair => (repair.OriginalAudioPath, repair.RepairedAudioPath))
            .Concat(fantasySoundtrackProjectRepair.AudioPathRepairs
                .Select(repair => (repair.OriginalAudioPath, repair.RepairedAudioPath)))
            .Select(repair =>
            (
                OriginalIdentity: MusicAudioPathIdentity.NormalizeMusicIdentity(repair.OriginalAudioPath),
                RepairedPath: MusicAudioPathIdentity.NormalizeAssetPath(repair.RepairedAudioPath)
            ))
            .Where(repair =>
                !string.IsNullOrWhiteSpace(repair.OriginalIdentity) &&
                !string.IsNullOrWhiteSpace(repair.RepairedPath))
            .GroupBy(repair => repair.OriginalIdentity, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(repair => repair.RepairedPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        return new MusicAudioPathRepairResolver(repairs);
    }

    public IReadOnlyList<string> Resolve(string sourcePath)
    {
        var identity = MusicAudioPathIdentity.NormalizeMusicIdentity(sourcePath);
        return _repairedPathsByIdentity.TryGetValue(identity, out var repairedPaths)
            ? repairedPaths
            : Array.Empty<string>();
    }
}
