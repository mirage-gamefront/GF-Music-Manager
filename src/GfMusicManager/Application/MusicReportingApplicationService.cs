using GfMusicManager.Core.Analysis;

namespace GfMusicManager.Application;

public sealed record MusicReportingApplicationResult(
    MusicAuditReport Report,
    MusicAuditReportFiles Files);

public sealed class MusicReportingApplicationService
{
    private readonly MusicAuditReportBuilder _builder = new();

    public MusicReportingApplicationResult Write(
        MusicScanApplicationResult scanResult,
        string outputDirectory,
        string fileStem = "music-audit",
        int longPlacementThreshold = MusicAuditReportBuilder.DefaultLongPlacementThreshold)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        var report = _builder.Build(
            scanResult.Scan,
            scanResult.MusicAnalysis,
            longPlacementThreshold,
            scanResult.Request.IncludeDisabledMods);
        var files = MusicAuditReportWriter.Write(outputDirectory, fileStem, report);
        return new MusicReportingApplicationResult(report, files);
    }
}
