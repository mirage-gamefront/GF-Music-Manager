using GfMusicManager.Core.Analysis;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Scanning;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class RealMo2AudioDuplicateDetectorTests
{
    [Fact]
    [Trait("Category", "RealMo2Audit")]
    public void ActualMo2Profile_FindsContentDuplicateCandidates()
    {
        var mo2Root = Environment.GetEnvironmentVariable("GF_MUSIC_AUDIT_MO2_ROOT");
        if (string.IsNullOrWhiteSpace(mo2Root) || !Directory.Exists(mo2Root))
        {
            Console.WriteLine(
                "REAL_MO2_AUDIO_DUPLICATE_NOT_RUN: GF_MUSIC_AUDIT_MO2_ROOT was not provided.");
            return;
        }

        var profileName = Environment.GetEnvironmentVariable("GF_MUSIC_AUDIT_PROFILE");
        var scanner = new Mo2Scanner();
        var result = scanner.Scan(new ScanOptions
        {
            Mo2Root = mo2Root,
            ProfileName = string.IsNullOrWhiteSpace(profileName) ? null : profileName,
            IncludeDisabledMods = false,
            ReadPluginRecords = true,
            ScanArchives = true,
            ScanLooseAssets = true
        });
        var duplicates = new AudioDuplicateDetector().Detect(result.Assets);

        Console.WriteLine(
            $"REAL_MO2_AUDIO_DUPLICATE_RESULT: assets={result.Assets.Count}, " +
            $"groups={duplicates.Groups.Count}, path={duplicates.PathConflictCount}, " +
            $"content={duplicates.ContentMatchCount}, similar={duplicates.SimilarCandidateCount}, " +
            $"readFailures={duplicates.ReadFailureCount}, " +
            $"similarityDecoder={duplicates.SimilarityDecoderAvailable}");
        Assert.NotEmpty(result.Assets);
        Assert.NotEmpty(duplicates.Groups.Where(group => group.Kind == AudioDuplicateKind.ContentMatch));
    }
}
