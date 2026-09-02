using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Localization;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Scanning;

namespace GfMusicManager.Application;

/// <summary>
/// WPF and CLI entry point for the complete read-only scan and analysis pipeline.
/// It contains no UI state and never changes MO2 or source mods.
/// </summary>
public sealed class MusicScanApplicationService
{
    private readonly Mo2Scanner _scanner;
    private readonly MusicSettingsAnalyzer _musicSettingsAnalyzer;
    private readonly AudioDuplicateDetector _audioDuplicateDetector;

    public MusicScanApplicationService(
        Mo2Scanner? scanner = null,
        MusicSettingsAnalyzer? musicSettingsAnalyzer = null,
        AudioDuplicateDetector? audioDuplicateDetector = null)
    {
        _scanner = scanner ?? new Mo2Scanner();
        _musicSettingsAnalyzer = musicSettingsAnalyzer ?? new MusicSettingsAnalyzer();
        _audioDuplicateDetector = audioDuplicateDetector ?? new AudioDuplicateDetector();
    }

    public MusicScanApplicationResult Scan(
        MusicScanRequest request,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = request.ToCoreOptions();
        GfMusicManagerLog.Info(
            $"MusicScanApplicationService: begin. root={options.Mo2Root}, " +
            $"profile={options.ProfileName ?? "<default>"}, " +
            $"includeDisabled={options.IncludeDisabledMods}, " +
            $"includeGenerated={request.IncludeGeneratedProduct}.");

        cancellationToken.ThrowIfCancellationRequested();
        var scan = _scanner.Scan(options, progress, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress(
            ScanIssueSeverity.Info,
            "MusicAnalysis",
            UiText.Get("Progress.MusicAnalysis"),
            0,
            1));
        var musicAnalysis = _musicSettingsAnalyzer.Analyze(scan);

        cancellationToken.ThrowIfCancellationRequested();
        var audioDuplicates = _audioDuplicateDetector.Detect(
            scan.Assets,
            progress,
            cancellationToken);

        var result = new MusicScanApplicationResult(
            request,
            scan,
            musicAnalysis,
            audioDuplicates);
        GfMusicManagerLog.Info(
            $"MusicScanApplicationService: complete. mods={scan.Mods.Count}, " +
            $"plugins={scan.Plugins.Count}, assets={scan.Assets.Count}, " +
            $"settings={musicAnalysis.Settings.Count}, " +
            $"duplicateGroups={audioDuplicates.Groups.Count}.");
        return result;
    }
}
