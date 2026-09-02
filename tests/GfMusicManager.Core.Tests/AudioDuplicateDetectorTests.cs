using System.Text;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class AudioDuplicateDetectorTests
{
    [Fact]
    public void DetectsPathConflictAndContentMatchAsSeparateWarnings()
    {
        var directory = Directory.CreateTempSubdirectory("gf-music-duplicate-test-");
        try
        {
            var firstBytes = CreateWave(samples => (short)(Math.Sin(samples * 0.08) * 12000));
            var secondBytes = CreateWave(samples => (short)(Math.Cos(samples * 0.11) * 12000));
            var firstPath = Write(directory.FullName, "first.wav", firstBytes);
            var secondPath = Write(directory.FullName, "second.wav", secondBytes);
            var thirdPath = Write(directory.FullName, "third.wav", firstBytes);
            var assets = new[]
            {
                Asset("Mod A", "music\\shared.xwm", firstPath),
                Asset("Mod B", "music\\shared.xwm", secondPath),
                Asset("Mod C", "music\\other.xwm", thirdPath)
            };

            var result = new AudioDuplicateDetector(ffmpegPath: string.Empty).Detect(assets);

            var pathGroup = Assert.Single(result.Groups.Where(group => group.Kind == AudioDuplicateKind.PathConflict));
            Assert.Equal("music\\shared.xwm", pathGroup.Subject);
            Assert.Equal(2, pathGroup.Sources.Count);

            var contentGroup = Assert.Single(result.Groups.Where(group => group.Kind == AudioDuplicateKind.ContentMatch));
            Assert.Equal(2, contentGroup.Sources.Count);
            Assert.Empty(result.Groups.Where(group => group.Kind == AudioDuplicateKind.SimilarCandidate));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void DetectsSimilarWaveformsAfterVolumeAndLeadingSilenceChanges()
    {
        var directory = Directory.CreateTempSubdirectory("gf-music-similar-test-");
        try
        {
            var firstBytes = CreateWave(
                samples => (short)(Math.Sin(samples * 0.08) * 12000));
            var secondBytes = CreateWave(
                samples => samples < 2400
                    ? (short)0
                    : (short)(Math.Sin((samples - 2400) * 0.08) * 6000));
            var firstPath = Write(directory.FullName, "first.wav", firstBytes);
            var secondPath = Write(directory.FullName, "second.wav", secondBytes);
            var assets = new[]
            {
                Asset("Mod A", "music\\first.xwm", firstPath),
                Asset("Mod B", "music\\second.xwm", secondPath)
            };

            var result = new AudioDuplicateDetector(ffmpegPath: string.Empty).Detect(assets);

            var similar = Assert.Single(result.Groups);
            Assert.Equal(AudioDuplicateKind.SimilarCandidate, similar.Kind);
            Assert.Equal(2, similar.Sources.Count);
            Assert.True(similar.SimilarityScore >= 0.96);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void IgnoresDuplicatesContainedWithinOneMod()
    {
        var directory = Directory.CreateTempSubdirectory("gf-music-same-mod-duplicate-test-");
        try
        {
            var firstBytes = CreateWave(samples => (short)(Math.Sin(samples * 0.08) * 12000));
            var similarBytes = CreateWave(samples => samples < 2400
                ? (short)0
                : (short)(Math.Sin((samples - 2400) * 0.08) * 6000));
            var firstPath = Write(directory.FullName, "first.wav", firstBytes);
            var samePathBytes = Write(directory.FullName, "same-path.wav", CreateWave(samples => (short)(Math.Cos(samples * 0.11) * 12000)));
            var sameContentPath = Write(directory.FullName, "same-content.wav", firstBytes);
            var similarPath = Write(directory.FullName, "similar.wav", similarBytes);
            var modPath = Path.Combine(directory.FullName, "Mod A");
            var assets = new[]
            {
                Asset("Mod A", "music\\shared.xwm", firstPath, modPath),
                Asset("Mod A", "music\\shared.xwm", samePathBytes, modPath),
                Asset("Mod A", "music\\other.xwm", sameContentPath, modPath),
                Asset("Mod A", "music\\similar.xwm", similarPath, modPath)
            };

            var result = new AudioDuplicateDetector(ffmpegPath: string.Empty).Detect(assets);

            Assert.Empty(result.Groups);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void DefaultPathConflictSelectionKeepsTheLoadOrderWinnerOnly()
    {
        var first = AssetWithoutFile("Mod A", "music\\shared.xwm", isVfsWinner: false);
        var second = AssetWithoutFile("Mod B", "music\\shared.xwm", isVfsWinner: true);
        var group = new AudioDuplicateGroup(
            "path:music\\shared.xwm",
            AudioDuplicateKind.PathConflict,
            "music\\shared.xwm",
            "path conflict",
            "test",
            new[]
            {
                new AudioDuplicateSource(first, "hash-a"),
                new AudioDuplicateSource(second, "hash-b")
            });

        var winners = AudioDuplicateDefaultSelection.SelectPathConflictWinners(
            new[] { group },
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Mod A"] = 20,
                ["Mod B"] = 10
            });

        Assert.Contains(MusicGenerationPlanEntry.CreateAssetKey(second), winners);
        Assert.DoesNotContain(MusicGenerationPlanEntry.CreateAssetKey(first), winners);
        Assert.Single(winners);
    }

    [Fact]
    public void ReportsSeparateConflictCheckProgressForReadFingerprintAndCompare()
    {
        var directory = Directory.CreateTempSubdirectory("gf-music-progress-test-");
        try
        {
            var firstBytes = CreateWave(samples => (short)(Math.Sin(samples * 0.08) * 12000));
            var secondBytes = CreateWave(samples => samples < 2400
                ? (short)0
                : (short)(Math.Sin((samples - 2400) * 0.08) * 6000));
            var firstPath = Write(directory.FullName, "first.wav", firstBytes);
            var secondPath = Write(directory.FullName, "second.wav", secondBytes);
            var assets = new[]
            {
                Asset("Mod A", "music\\first.xwm", firstPath),
                Asset("Mod B", "music\\second.xwm", secondPath)
            };
            var reports = new List<ScanProgress>();

            var result = new AudioDuplicateDetector(ffmpegPath: string.Empty).Detect(
                assets,
                new RecordingProgress(reports));

            Assert.NotEmpty(result.Groups);
            Assert.Contains(reports, report => report.Stage == "ConflictRead" && report.Current == 0 && report.Total == 2);
            Assert.Contains(reports, report => report.Stage == "ConflictRead" && report.Current == 2 && report.Total == 2);
            Assert.Contains(reports, report => report.Stage == "ConflictFingerprint" && report.Current == 2 && report.Total == 2);
            Assert.Contains(reports, report => report.Stage == "ConflictCompare" && report.Current == 1 && report.Total == 1);
            Assert.Contains(reports, report => report.Stage == "ConflictFinalize" && report.Current == 1 && report.Total == 1);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void ProducesSameResultForSequentialAndParallelFingerprinting()
    {
        var directory = Directory.CreateTempSubdirectory("gf-music-parallel-equivalence-test-");
        try
        {
            var assets = Enumerable.Range(0, 8)
                .Select(index =>
                {
                    var frequency = 0.045 + (index * 0.011);
                    var amplitude = 6000 + (index * 500);
                    var bytes = CreateWave(samples =>
                        (short)(Math.Sin(samples * frequency) * amplitude));
                    var path = Write(directory.FullName, $"track-{index}.wav", bytes);
                    return Asset(
                        $"Mod {index}",
                        $"music\\track-{index}.xwm",
                        path);
                })
                .ToArray();

            var sequential = new AudioDuplicateDetector(
                ffmpegPath: string.Empty,
                fingerprintMaxDegreeOfParallelism: 1).Detect(assets);
            var parallel = new AudioDuplicateDetector(
                ffmpegPath: string.Empty,
                fingerprintMaxDegreeOfParallelism: 4).Detect(assets);

            AssertEquivalent(sequential, parallel);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private static void AssertEquivalent(
        AudioDuplicateAnalysisResult expected,
        AudioDuplicateAnalysisResult actual)
    {
        Assert.Equal(expected.AnalyzedSourceCount, actual.AnalyzedSourceCount);
        Assert.Equal(expected.ReadFailureCount, actual.ReadFailureCount);
        Assert.Equal(expected.SimilarityComparisonCount, actual.SimilarityComparisonCount);
        Assert.Equal(expected.SimilarityDecoderAvailable, actual.SimilarityDecoderAvailable);
        Assert.Equal(expected.Groups.Count, actual.Groups.Count);

        for (var groupIndex = 0; groupIndex < expected.Groups.Count; groupIndex++)
        {
            var expectedGroup = expected.Groups[groupIndex];
            var actualGroup = actual.Groups[groupIndex];
            Assert.Equal(expectedGroup.GroupId, actualGroup.GroupId);
            Assert.Equal(expectedGroup.Kind, actualGroup.Kind);
            Assert.Equal(expectedGroup.Subject, actualGroup.Subject);
            Assert.Equal(expectedGroup.Explanation, actualGroup.Explanation);
            Assert.Equal(expectedGroup.DetectionMethod, actualGroup.DetectionMethod);
            Assert.Equal(expectedGroup.Sources.Count, actualGroup.Sources.Count);

            if (expectedGroup.SimilarityScore is { } expectedScore &&
                actualGroup.SimilarityScore is { } actualScore)
            {
                Assert.Equal(expectedScore, actualScore, precision: 12);
            }
            else
            {
                Assert.Equal(expectedGroup.SimilarityScore, actualGroup.SimilarityScore);
            }

            for (var sourceIndex = 0; sourceIndex < expectedGroup.Sources.Count; sourceIndex++)
            {
                var expectedSource = expectedGroup.Sources[sourceIndex];
                var actualSource = actualGroup.Sources[sourceIndex];
                Assert.Equal(expectedSource.AssetKey, actualSource.AssetKey);
                Assert.Equal(expectedSource.ContentHash, actualSource.ContentHash);
                Assert.Equal(expectedSource.DurationSeconds, actualSource.DurationSeconds);
            }
        }
    }

    private static AssetSource Asset(
        string modName,
        string virtualPath,
        string sourcePath,
        string? modPath = null) =>
        new(
            virtualPath,
            AssetSourceKind.Loose,
            modName,
            modPath ?? Path.Combine(Path.GetDirectoryName(sourcePath)!, modName),
            true,
            sourcePath,
            null,
            new FileInfo(sourcePath).Length)
        {
            IsVfsWinner = modName.Equals("Mod A", StringComparison.OrdinalIgnoreCase)
        };

    private static AssetSource AssetWithoutFile(
        string modName,
        string virtualPath,
        bool isVfsWinner) =>
        new(
            virtualPath,
            AssetSourceKind.Loose,
            modName,
            @"C:\Fixture\" + modName,
            true,
            @"C:\Fixture\" + modName + "\\track.xwm",
            null,
            1)
        {
            IsVfsWinner = isVfsWinner
        };

    private static string Write(string directory, string fileName, byte[] bytes)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] CreateWave(Func<int, short> sampleFactory)
    {
        const int sampleRate = 8000;
        const int sampleCount = sampleRate * 3;
        var pcm = new byte[sampleCount * 2];
        for (var index = 0; index < sampleCount; index++)
        {
            BitConverter.TryWriteBytes(
                pcm.AsSpan(index * 2, 2),
                sampleFactory(index));
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
            writer.Flush();
        return stream.ToArray();
    }

    private sealed class RecordingProgress(ICollection<ScanProgress> reports) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => reports.Add(value);
    }
}
