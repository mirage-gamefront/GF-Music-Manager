using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Archives;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Analysis;

/// <summary>
/// Finds the three user-facing audio duplicate categories without changing source mods.
/// Exact content uses SHA-256. Similar candidates use a normalized mono waveform fingerprint
/// produced by FFmpeg when available (or directly from PCM WAV data).
/// </summary>
public sealed class AudioDuplicateDetector
{
    private const int ComparisonProgressInterval = 512;
    private const int FingerprintProgressInterval = 16;

    private readonly BsaArchiveReader _archiveReader;
    private readonly string? _ffmpegPath;
    private readonly int _fingerprintMaxDegreeOfParallelism;

    public AudioDuplicateDetector(
        BsaArchiveReader? archiveReader = null,
        string? ffmpegPath = null,
        int? fingerprintMaxDegreeOfParallelism = null)
    {
        _archiveReader = archiveReader ?? new BsaArchiveReader();
        _ffmpegPath = ffmpegPath ?? ResolveFfmpegPath();
        _fingerprintMaxDegreeOfParallelism = fingerprintMaxDegreeOfParallelism ??
            ResolveFingerprintMaxDegreeOfParallelism();
        if (_fingerprintMaxDegreeOfParallelism < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fingerprintMaxDegreeOfParallelism),
                "Fingerprintの同時実行数は1以上で指定してください。");
        }
    }

    public AudioDuplicateAnalysisResult Detect(
        IReadOnlyList<AssetSource> assets,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assets);

        var sources = new List<AnalyzedSource>(assets.Count);
        var readFailures = 0;
        var similarityDecoderAvailable = _ffmpegPath is not null;
        GfMusicManagerLog.Info(
            $"AudioDuplicateDetector: begin. assets={assets.Count}, " +
            $"ffmpeg={_ffmpegPath ?? "unavailable"}, " +
            $"fingerprintDegree={_fingerprintMaxDegreeOfParallelism}.");

        progress?.Report(new ScanProgress(
            ScanIssueSeverity.Info,
            "ConflictRead",
            UiText.Get("Progress.ConflictRead"),
            0,
            assets.Count));

        for (var index = 0; index < assets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var asset = assets[index];
            try
            {
                var bytes = ReadAsset(asset);
                var contentHash = Convert.ToHexString(SHA256.HashData(bytes));
                var durationSeconds = AudioDurationEstimator.TryEstimate(bytes);
                sources.Add(new AnalyzedSource(asset, contentHash, durationSeconds, null));
            }
            catch (Exception exception)
            {
                readFailures++;
                GfMusicManagerLog.Warning(
                    $"AudioDuplicateDetector: source read failed. mod={asset.ModName}, " +
                    $"path={asset.VirtualPath}, source={asset.DisplaySource}, " +
                    $"error={exception.Message}");
            }

            progress?.Report(new ScanProgress(
                ScanIssueSeverity.Info,
                "ConflictRead",
                UiText.Get("Progress.ConflictRead"),
                index + 1,
                assets.Count,
                asset.ModName,
                asset.VirtualPath));
        }

        var groups = new List<AudioDuplicateGroup>();
        var pathGroups = sources
            .GroupBy(source => NormalizePath(source.Asset.VirtualPath), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Where(group => ContainsMultipleMods(group.Select(source => source.Asset)))
            .ToArray();
        foreach (var group in pathGroups)
        {
            groups.Add(new AudioDuplicateGroup(
                $"path:{group.Key}",
                AudioDuplicateKind.PathConflict,
                group.Key,
                "同じゲーム内パスに複数の音源実体があります。出力MODでは同じパスを同時に使えないため、採用する実体を1つ選択してください。",
                "正規化したゲーム内音源パスの一致",
                ToSources(group)));
        }

        var contentGroups = sources
            .GroupBy(source => source.ContentHash, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Where(group => group.Select(source => NormalizePath(source.Asset.VirtualPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Where(group => ContainsMultipleMods(group.Select(source => source.Asset)))
            .ToArray();
        foreach (var group in contentGroups)
        {
            var subject = group.First().ContentHash[..12];
            groups.Add(new AudioDuplicateGroup(
                $"content:{group.Key}",
                AudioDuplicateKind.ContentMatch,
                subject,
                "ゲーム内パスは異なりますが、音声ファイルの内容が一致しています。ゲーム上のパス競合ではないため、両方を別音源として採用できます。",
                "音声ファイル全体のSHA-256一致",
                ToSources(group)));
        }

        var contentPairKeys = contentGroups
            .SelectMany(group => CreatePairKeys(group.Select(source => source.AssetKey)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var potentialSimilarSources = SelectPotentialSimilarSources(sources, contentPairKeys);
        GfMusicManagerLog.Info(
            $"AudioDuplicateDetector: similar prefilter. sources={potentialSimilarSources.Count}, " +
            $"total={sources.Count}.");
        var potentialSimilarSourceIndices = potentialSimilarSources
            .Order()
            .ToArray();
        progress?.Report(new ScanProgress(
            ScanIssueSeverity.Info,
            "ConflictFingerprint",
            UiText.Get("Progress.ConflictFingerprint"),
            0,
            potentialSimilarSourceIndices.Length));

        // Each fingerprint is independent. Keep the source list unchanged while worker
        // threads run, then apply the results by source index after the parallel phase.
        // This preserves the old source order and keeps group construction deterministic.
        var fingerprintResults = new AudioWaveformFingerprint?[sources.Count];
        var fingerprintCurrent = 0;
        var lastReportedFingerprintCurrent = 0;
        var fingerprintProgressLock = new object();

        void ProcessFingerprint(int sourceIndex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[sourceIndex];
            try
            {
                var bytes = ReadAsset(source.Asset);
                fingerprintResults[sourceIndex] = AudioWaveformFingerprint.TryCreate(
                    source.Asset,
                    bytes,
                    _ffmpegPath,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                GfMusicManagerLog.Warning(
                    $"AudioDuplicateDetector: candidate waveform read failed. " +
                    $"mod={source.Asset.ModName}, path={source.Asset.VirtualPath}, error={exception.Message}");
            }

            var completed = Interlocked.Increment(ref fingerprintCurrent);
            lock (fingerprintProgressLock)
            {
                var isFinal = completed == potentialSimilarSourceIndices.Length;
                var reachedInterval = completed - lastReportedFingerprintCurrent >= FingerprintProgressInterval;
                if (isFinal || reachedInterval)
                {
                    lastReportedFingerprintCurrent = Math.Max(
                        lastReportedFingerprintCurrent,
                        completed);
                    progress?.Report(new ScanProgress(
                        ScanIssueSeverity.Info,
                        "ConflictFingerprint",
                        UiText.Get("Progress.ConflictFingerprint"),
                        lastReportedFingerprintCurrent,
                        potentialSimilarSourceIndices.Length,
                        source.Asset.ModName,
                        source.Asset.VirtualPath));
                }
            }
        }

        if (_fingerprintMaxDegreeOfParallelism == 1)
        {
            foreach (var sourceIndex in potentialSimilarSourceIndices)
            {
                ProcessFingerprint(sourceIndex);
            }
        }
        else
        {
            Parallel.ForEach(
                potentialSimilarSourceIndices,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = _fingerprintMaxDegreeOfParallelism
                },
                ProcessFingerprint);
        }

        foreach (var sourceIndex in potentialSimilarSourceIndices)
        {
            sources[sourceIndex] = sources[sourceIndex] with
            {
                Fingerprint = fingerprintResults[sourceIndex]
            };
        }
        var similarGroups = DetectSimilarGroups(
            sources,
            contentPairKeys,
            progress,
            out var comparisonCount,
            cancellationToken);
        groups.AddRange(similarGroups);

        groups = groups
            .OrderBy(group => group.Kind)
            .ThenBy(group => group.Subject, StringComparer.OrdinalIgnoreCase)
            .ToList();
        GfMusicManagerLog.Info(
            $"AudioDuplicateDetector: complete. analyzed={sources.Count}, failures={readFailures}, " +
            $"path={pathGroups.Length}, content={contentGroups.Length}, " +
            $"similar={similarGroups.Count}, comparisons={comparisonCount}.");
        progress?.Report(new ScanProgress(
            ScanIssueSeverity.Info,
            "ConflictFinalize",
            UiText.Get("Progress.ConflictFinalize"),
            1,
            1));

        return new AudioDuplicateAnalysisResult(
            groups,
            sources.Count,
            readFailures,
            comparisonCount,
            similarityDecoderAvailable);
    }

    private IReadOnlyList<AudioDuplicateGroup> DetectSimilarGroups(
        IReadOnlyList<AnalyzedSource> sources,
        IReadOnlySet<string> contentPairKeys,
        IProgress<ScanProgress>? progress,
        out int comparisonCount,
        CancellationToken cancellationToken)
    {
        comparisonCount = 0;
        var candidates = sources
            .Where(source => source.Fingerprint is not null)
            .ToArray();
        var comparablePairs = CreateComparablePairs(
            candidates,
            contentPairKeys,
            cancellationToken);
        var comparisonTotal = comparablePairs.Count;
        progress?.Report(new ScanProgress(
            ScanIssueSeverity.Info,
            "ConflictCompare",
            UiText.Get("Progress.ConflictCompare"),
            0,
            comparisonTotal));
        if (candidates.Length < 2)
        {
            return Array.Empty<AudioDuplicateGroup>();
        }

        var parent = Enumerable.Range(0, candidates.Length).ToArray();
        var bestScores = new Dictionary<int, double>();
        foreach (var pair in comparablePairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = candidates[pair.LeftIndex];
            var right = candidates[pair.RightIndex];

            comparisonCount++;
            if (comparisonCount % ComparisonProgressInterval == 0 ||
                comparisonCount == comparisonTotal)
            {
                progress?.Report(new ScanProgress(
                    ScanIssueSeverity.Info,
                    "ConflictCompare",
                    UiText.Get("Progress.ConflictCompare"),
                    comparisonCount,
                    comparisonTotal));
            }
            var score = AudioWaveformFingerprint.Compare(
                left.Fingerprint!,
                right.Fingerprint!);
            if (score < 0.96)
            {
                continue;
            }

            Union(parent, pair.LeftIndex, pair.RightIndex);
            var root = Find(parent, pair.LeftIndex);
            bestScores[root] = Math.Max(
                bestScores.TryGetValue(root, out var existing) ? existing : 0,
                score);
        }

        var components = Enumerable.Range(0, candidates.Length)
            .GroupBy(index => Find(parent, index))
            .Where(group => group.Count() > 1)
            .ToArray();
        var groups = new List<AudioDuplicateGroup>(components.Length);
        foreach (var component in components)
        {
            var componentSources = component
                .Select(index => candidates[index])
                .ToArray();
            var subject = string.Join(", ", componentSources.Select(source => source.Asset.ModName).Distinct(StringComparer.OrdinalIgnoreCase));
            var score = component
                .Select(index =>
                {
                    var root = Find(parent, index);
                    return bestScores.TryGetValue(root, out var value) ? value : 0;
                })
                .DefaultIfEmpty()
                .Max();
            groups.Add(new AudioDuplicateGroup(
                $"similar:{string.Join("|", componentSources.Select(source => source.AssetKey).Order(StringComparer.OrdinalIgnoreCase))}",
                AudioDuplicateKind.SimilarCandidate,
                subject,
                "音量・再エンコード・前後の無音部分などの差を含め、同じ音源の可能性があります。自動では除外せず、試聴して判断してください。",
                "正規化したモノラル波形の特徴量比較",
                ToSources(componentSources),
                score));
        }

        return groups;
    }

    public static bool ContainsMultipleMods(IEnumerable<AssetSource> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        return assets
            .Select(GetModIdentity)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(1)
            .Any();
    }

    private static IReadOnlySet<int> SelectPotentialSimilarSources(
        IReadOnlyList<AnalyzedSource> sources,
        IReadOnlySet<string> contentPairKeys)
    {
        var selected = new HashSet<int>();
        for (var leftIndex = 0; leftIndex < sources.Count; leftIndex++)
        {
            var left = sources[leftIndex];
            if (left.DurationSeconds is null)
            {
                continue;
            }

            for (var rightIndex = leftIndex + 1; rightIndex < sources.Count; rightIndex++)
            {
                var right = sources[rightIndex];
                if (right.DurationSeconds is null ||
                    string.Equals(
                        NormalizePath(left.Asset.VirtualPath),
                        NormalizePath(right.Asset.VirtualPath),
                        StringComparison.OrdinalIgnoreCase) ||
                    AreFromSameMod(left, right) ||
                    contentPairKeys.Contains(CreatePairKey(left.AssetKey, right.AssetKey)))
                {
                    continue;
                }

                var durationTolerance = Math.Max(
                    1.0,
                    Math.Max(left.DurationSeconds.Value, right.DurationSeconds.Value) * 0.02);
                if (Math.Abs(left.DurationSeconds.Value - right.DurationSeconds.Value) > durationTolerance)
                {
                    continue;
                }

                var leftLength = left.Asset.Length ?? 0;
                var rightLength = right.Asset.Length ?? 0;
                if (leftLength > 0 && rightLength > 0)
                {
                    var sizeRatio = (double)Math.Min(leftLength, rightLength) /
                                    Math.Max(leftLength, rightLength);
                    if (sizeRatio < 0.20)
                    {
                        continue;
                    }
                }

                selected.Add(leftIndex);
                selected.Add(rightIndex);
            }
        }

        return selected;
    }

    private byte[] ReadAsset(AssetSource asset)
    {
        if (asset.IsFromArchive)
        {
            return _archiveReader.ReadEntry(
                asset.SourcePath,
                asset.ArchiveEntryPath ?? asset.VirtualPath);
        }

        if (!File.Exists(asset.SourcePath))
        {
            throw new FileNotFoundException("音源ファイルが見つかりません。", asset.SourcePath);
        }

        return File.ReadAllBytes(asset.SourcePath);
    }

    private static IReadOnlyList<AudioDuplicateSource> ToSources(IEnumerable<AnalyzedSource> sources) =>
        sources
            .OrderByDescending(source => source.Asset.IsVfsWinner && source.Asset.ModEnabled)
            .ThenBy(source => source.Asset.ModName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Asset.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .Select(source => new AudioDuplicateSource(
                source.Asset,
                source.ContentHash,
                source.Fingerprint?.DurationSeconds ?? source.DurationSeconds))
            .ToArray();

    private static IEnumerable<string> CreatePairKeys(IEnumerable<string> assetKeys)
    {
        var keys = assetKeys.ToArray();
        for (var left = 0; left < keys.Length; left++)
        {
            for (var right = left + 1; right < keys.Length; right++)
            {
                yield return CreatePairKey(keys[left], keys[right]);
            }
        }
    }

    private static string CreatePairKey(string left, string right) =>
        string.Compare(left, right, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{left}\u001f{right}"
            : $"{right}\u001f{left}";

    private static IReadOnlyList<ComparablePair> CreateComparablePairs(
        IReadOnlyList<AnalyzedSource> candidates,
        IReadOnlySet<string> contentPairKeys,
        CancellationToken cancellationToken)
    {
        var pairs = new List<ComparablePair>();
        for (var leftIndex = 0; leftIndex < candidates.Count; leftIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var rightIndex = leftIndex + 1; rightIndex < candidates.Count; rightIndex++)
            {
                if (CanCompare(candidates[leftIndex], candidates[rightIndex], contentPairKeys))
                {
                    pairs.Add(new ComparablePair(leftIndex, rightIndex));
                }
            }
        }

        return pairs;
    }

    private readonly record struct ComparablePair(int LeftIndex, int RightIndex);

    private static bool CanCompare(
        AnalyzedSource left,
        AnalyzedSource right,
        IReadOnlySet<string> contentPairKeys) =>
        !string.Equals(
            NormalizePath(left.Asset.VirtualPath),
            NormalizePath(right.Asset.VirtualPath),
            StringComparison.OrdinalIgnoreCase) &&
        !AreFromSameMod(left, right) &&
        !contentPairKeys.Contains(CreatePairKey(left.AssetKey, right.AssetKey));

    private static bool AreFromSameMod(AnalyzedSource left, AnalyzedSource right) =>
        string.Equals(
            GetModIdentity(left.Asset),
            GetModIdentity(right.Asset),
            StringComparison.OrdinalIgnoreCase);

    private static string GetModIdentity(AssetSource asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.ModPath))
        {
            try
            {
                return Path.GetFullPath(asset.ModPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (ArgumentException)
            {
                // Fall back to the stable display identity when a malformed test/source path is supplied.
            }
        }

        return asset.ModName.Trim();
    }

    private static string NormalizePath(string path) => path
        .Replace('/', '\\')
        .TrimStart('\\');

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    private static void Union(int[] parent, int left, int right)
    {
        var leftRoot = Find(parent, left);
        var rightRoot = Find(parent, right);
        if (leftRoot != rightRoot)
        {
            parent[rightRoot] = leftRoot;
        }
    }

    private static string? ResolveFfmpegPath()
    {
        var configured = Environment.GetEnvironmentVariable("GF_MUSIC_MANAGER_FFMPEG");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "ffmpeg.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            var path = output
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => File.Exists(line.Trim()));
            return string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Warning($"AudioDuplicateDetector: ffmpeg lookup failed: {exception.Message}");
            return null;
        }
    }

    private static int ResolveFingerprintMaxDegreeOfParallelism()
    {
        var processorCount = Math.Max(1, Environment.ProcessorCount);
        return processorCount == 1
            ? 1
            : Math.Clamp(processorCount / 2, 2, 4);
    }

    private sealed record AnalyzedSource(
        AssetSource Asset,
        string ContentHash,
        double? DurationSeconds,
        AudioWaveformFingerprint? Fingerprint)
    {
        public string AssetKey => MusicGenerationPlanEntry.CreateAssetKey(Asset);
    }
}

internal static class AudioDurationEstimator
{
    public static double? TryEstimate(byte[] bytes)
    {
        if (bytes.Length < 12 ||
            !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            (!bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8) &&
             !bytes.AsSpan(8, 4).SequenceEqual("XWMA"u8)))
        {
            return null;
        }

        var isXwma = bytes.AsSpan(8, 4).SequenceEqual("XWMA"u8);
        var offset = 12;
        var channels = 0;
        var sampleRate = 0u;
        var bitsPerSample = 0;
        var audioFormat = 0;
        var dataLength = 0u;
        uint? decodedPacketEnd = null;
        while (offset + 8 <= bytes.Length)
        {
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(offset + 4, 4));
            var dataOffset = offset + 8;
            if (chunkSize > int.MaxValue || dataOffset > bytes.Length - (int)chunkSize)
            {
                return null;
            }

            var size = (int)chunkSize;
            switch (System.Text.Encoding.ASCII.GetString(bytes, offset, 4))
            {
                case "fmt ":
                    if (size >= 16)
                    {
                        audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(
                            bytes.AsSpan(dataOffset, 2));
                        channels = BinaryPrimitives.ReadUInt16LittleEndian(
                            bytes.AsSpan(dataOffset + 2, 2));
                        sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(
                            bytes.AsSpan(dataOffset + 4, 4));
                        bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(
                            bytes.AsSpan(dataOffset + 14, 2));
                    }

                    break;
                case "data":
                    dataLength = chunkSize;
                    break;
                case "dpds":
                    if (size >= 4 && size % 4 == 0)
                    {
                        decodedPacketEnd = BinaryPrimitives.ReadUInt32LittleEndian(
                            bytes.AsSpan(dataOffset + size - 4, 4));
                    }

                    break;
            }

            offset = dataOffset + size + (size & 1);
        }

        if (channels <= 0 || sampleRate == 0 || bitsPerSample <= 0)
        {
            return null;
        }

        var decodedBytesPerSecond = (double)channels * bitsPerSample / 8 * sampleRate;
        if (decodedBytesPerSecond <= 0)
        {
            return null;
        }

        if (isXwma && decodedPacketEnd is not null)
        {
            return decodedPacketEnd.Value / decodedBytesPerSecond;
        }

        if (audioFormat == 1 && dataLength > 0)
        {
            return dataLength / decodedBytesPerSecond;
        }

        return null;
    }
}

internal sealed class AudioWaveformFingerprint
{
    private const int FeatureCount = 64;

    private AudioWaveformFingerprint(double[] rms, double[] crossings, double durationSeconds)
    {
        Rms = rms;
        Crossings = crossings;
        DurationSeconds = durationSeconds;
    }

    public double[] Rms { get; }
    public double[] Crossings { get; }
    public double DurationSeconds { get; }

    public static AudioWaveformFingerprint? TryCreate(
        AssetSource asset,
        byte[] bytes,
        string? ffmpegPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pcm = TryReadPcmWave(bytes, out var sampleRate);
        if (pcm is not null)
        {
            return CreateFromPcm(pcm, sampleRate);
        }

        if (string.IsNullOrWhiteSpace(ffmpegPath))
        {
            return null;
        }

        var inputPath = asset.SourceKind == AssetSourceKind.Loose && File.Exists(asset.SourcePath)
            ? asset.SourcePath
            : CreateTemporaryInput(asset, bytes);
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            processStartInfo.ArgumentList.Add("-hide_banner");
            processStartInfo.ArgumentList.Add("-loglevel");
            processStartInfo.ArgumentList.Add("error");
            processStartInfo.ArgumentList.Add("-i");
            processStartInfo.ArgumentList.Add(inputPath);
            processStartInfo.ArgumentList.Add("-vn");
            processStartInfo.ArgumentList.Add("-ac");
            processStartInfo.ArgumentList.Add("1");
            processStartInfo.ArgumentList.Add("-ar");
            processStartInfo.ArgumentList.Add("8000");
            processStartInfo.ArgumentList.Add("-f");
            processStartInfo.ArgumentList.Add("s16le");
            processStartInfo.ArgumentList.Add("pipe:1");

            using var process = Process.Start(processStartInfo);
            if (process is null)
            {
                return null;
            }

            using var output = new MemoryStream();
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            Task.WaitAll(stdoutTask, stderrTask);
            process.WaitForExit();
            if (process.ExitCode != 0 || output.Length < 16000)
            {
                return null;
            }

            return CreateFromPcm(output.ToArray(), 8000);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Warning(
                $"AudioDuplicateDetector: waveform decode failed. path={asset.VirtualPath}, " +
                $"error={exception.Message}");
            return null;
        }
        finally
        {
            DeleteTemporaryInput(inputPath, asset);
        }
    }

    public static double Compare(AudioWaveformFingerprint left, AudioWaveformFingerprint right)
    {
        var durationRatio = Math.Min(left.DurationSeconds, right.DurationSeconds) /
                            Math.Max(left.DurationSeconds, right.DurationSeconds);
        if (durationRatio < 0.80)
        {
            return 0;
        }

        var rmsScore = Cosine(left.Rms, right.Rms);
        var crossingScore = left.Crossings
            .Zip(right.Crossings, (leftValue, rightValue) =>
            {
                var scale = Math.Max(0.01, Math.Max(leftValue, rightValue));
                return 1 - Math.Min(1, Math.Abs(leftValue - rightValue) / scale);
            })
            .DefaultIfEmpty(0)
            .Average();
        return (rmsScore * 0.65) + (crossingScore * 0.35);
    }

    private static AudioWaveformFingerprint? CreateFromPcm(byte[] pcm, int sampleRate)
    {
        if (pcm.Length < 2 || sampleRate <= 0)
        {
            return null;
        }

        var sampleCount = pcm.Length / 2;
        var samples = new double[sampleCount];
        var maximum = 0d;
        for (var index = 0; index < sampleCount; index++)
        {
            var value = BitConverter.ToInt16(pcm, index * 2) / 32768d;
            samples[index] = value;
            maximum = Math.Max(maximum, Math.Abs(value));
        }

        if (maximum < 0.0001)
        {
            return null;
        }

        var threshold = maximum * 0.01;
        var start = 0;
        while (start < samples.Length && Math.Abs(samples[start]) < threshold)
        {
            start++;
        }

        var end = samples.Length - 1;
        while (end > start && Math.Abs(samples[end]) < threshold)
        {
            end--;
        }

        var length = end - start + 1;
        if (length < sampleRate)
        {
            return null;
        }

        var rms = new double[FeatureCount];
        var crossings = new double[FeatureCount];
        for (var bucket = 0; bucket < FeatureCount; bucket++)
        {
            var bucketStart = start + (length * bucket / FeatureCount);
            var bucketEnd = start + (length * (bucket + 1) / FeatureCount);
            bucketEnd = Math.Max(bucketStart + 1, bucketEnd);
            var sumSquares = 0d;
            var crossingCount = 0;
            for (var index = bucketStart; index < Math.Min(bucketEnd, samples.Length); index++)
            {
                sumSquares += samples[index] * samples[index];
                if (index > bucketStart && Math.Sign(samples[index]) != Math.Sign(samples[index - 1]))
                {
                    crossingCount++;
                }
            }

            var count = Math.Max(1, Math.Min(bucketEnd, samples.Length) - bucketStart);
            rms[bucket] = Math.Sqrt(sumSquares / count);
            crossings[bucket] = (double)crossingCount / count;
        }

        Normalize(rms);
        return new AudioWaveformFingerprint(rms, crossings, (double)length / sampleRate);
    }

    private static byte[]? TryReadPcmWave(byte[] bytes, out int sampleRate)
    {
        sampleRate = 0;
        if (bytes.Length < 44 || bytes[0] != (byte)'R' || bytes[1] != (byte)'I' ||
            bytes[2] != (byte)'F' || bytes[3] != (byte)'F' ||
            bytes[8] != (byte)'W' || bytes[9] != (byte)'A' ||
            bytes[10] != (byte)'V' || bytes[11] != (byte)'E')
        {
            return null;
        }

        var offset = 12;
        var dataOffset = -1;
        var dataLength = 0;
        var audioFormat = 0;
        var channels = 0;
        var bitsPerSample = 0;
        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var length = BitConverter.ToInt32(bytes, offset + 4);
            offset += 8;
            if (length < 0 || offset + length > bytes.Length)
            {
                return null;
            }

            if (id == "fmt " && length >= 16)
            {
                audioFormat = BitConverter.ToInt16(bytes, offset);
                channels = BitConverter.ToInt16(bytes, offset + 2);
                sampleRate = BitConverter.ToInt32(bytes, offset + 4);
                bitsPerSample = BitConverter.ToInt16(bytes, offset + 14);
            }
            else if (id == "data")
            {
                dataOffset = offset;
                dataLength = length;
            }

            offset += length + (length & 1);
        }

        if (audioFormat != 1 || channels <= 0 || bitsPerSample != 16 ||
            sampleRate <= 0 || dataOffset < 0 || dataLength < 2)
        {
            sampleRate = 0;
            return null;
        }

        // The detector uses mono PCM. Downmixing is done while building the feature vector.
        // Return a compact mono stream so the feature code stays independent of RIFF layout.
        var frameSize = channels * 2;
        var frames = dataLength / frameSize;
        var mono = new byte[frames * 2];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += BitConverter.ToInt16(bytes, dataOffset + (frame * frameSize) + (channel * 2));
            }

            var value = (short)Math.Clamp(sum / channels, short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(mono.AsSpan(frame * 2, 2), value);
        }

        return mono;
    }

    private static void Normalize(double[] values)
    {
        var length = Math.Sqrt(values.Sum(value => value * value));
        if (length < 0.000001)
        {
            return;
        }

        for (var index = 0; index < values.Length; index++)
        {
            values[index] /= length;
        }
    }

    private static double Cosine(double[] left, double[] right)
    {
        var dot = 0d;
        var leftLength = 0d;
        var rightLength = 0d;
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            dot += left[index] * right[index];
            leftLength += left[index] * left[index];
            rightLength += right[index] * right[index];
        }

        return leftLength < 0.000001 || rightLength < 0.000001
            ? 0
            : dot / Math.Sqrt(leftLength * rightLength);
    }

    private static string CreateTemporaryInput(AssetSource asset, byte[] bytes)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gf-music-manager-audio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(asset.ArchiveEntryPath ?? asset.VirtualPath);
        var path = Path.Combine(directory, $"input{extension}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void DeleteTemporaryInput(string path, AssetSource asset)
    {
        if (asset.SourceKind == AssetSourceKind.Loose &&
            string.Equals(path, asset.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Temporary analysis files are best-effort cleanup only.
        }
    }
}
