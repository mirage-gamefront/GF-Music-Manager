using GfMusicManager.Core.Localization;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Analysis;

public static class MusicScanProgressFormatter
{
    public static string Format(ScanProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var stage = progress.Stage switch
        {
            "Plugin" => UiText.Get("Progress.Stage.Plugin"),
            "MOD" => UiText.Get("Progress.Stage.Mod"),
            "ResultPlan" => UiText.Get("Progress.Stage.AudioData"),
            "ResultPrepare" => UiText.Get("Progress.Stage.AudioList"),
            "ResultRestore" => UiText.Get("Progress.Stage.ExistingSettings"),
            "ResultFinalize" => UiText.Get("Progress.Stage.AudioList"),
            _ => progress.Stage
        };

        if (progress.Stage.StartsWith("Conflict", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Format(
                "Progress.ConflictMessage",
                GetConflictMessage(progress.Stage));
        }

        if (progress.Stage.Equals("Audio", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Get("Progress.ConflictRead");
        }

        if (progress.Stage.StartsWith("Result", StringComparison.OrdinalIgnoreCase))
        {
            return GetResultMessage(progress.Stage);
        }

        if (progress.Stage.Equals("MusicAnalysis", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Get("Progress.MusicAnalysis");
        }

        if (progress.Stage.Equals("Plugin", StringComparison.OrdinalIgnoreCase) &&
            (!string.IsNullOrWhiteSpace(progress.ModName) ||
             !string.IsNullOrWhiteSpace(progress.PluginName)))
        {
            return UiText.Format(
                "Progress.ModPlugin",
                progress.ModName ?? "—",
                progress.PluginName ?? "—");
        }

        if (progress.Stage.Equals("MOD", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(progress.ModName))
        {
            return UiText.Format("Progress.ModPlugin", progress.ModName, "—");
        }

        if (progress.Stage.Equals("BSA", StringComparison.OrdinalIgnoreCase))
        {
            var archiveName = Path.GetFileName(progress.SourcePath) ?? progress.Message;
            return UiText.Format(
                progress.Level == ScanIssueSeverity.Warning
                    ? "Progress.BsaFailed"
                    : "Progress.BsaIndexed",
                archiveName);
        }

        if (progress.Stage.Equals("LooseAsset", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Format("Progress.LooseAssetFailed", progress.ModName ?? "—");
        }

        return progress.Current is { } current && progress.Total is { } total
            ? UiText.Format("Progress.StageCount", stage, current, total)
            : progress.Message;
    }

    private static string GetConflictMessage(string stage) => stage switch
    {
        "ConflictRead" => UiText.Get("Progress.ConflictRead"),
        "ConflictFingerprint" => UiText.Get("Progress.ConflictFingerprint"),
        "ConflictCompare" => UiText.Get("Progress.ConflictCompare"),
        "ConflictFinalize" => UiText.Get("Progress.ConflictFinalize"),
        _ => UiText.Get("Progress.ConflictChecking")
    };

    private static string GetResultMessage(string stage) => stage switch
    {
        "ResultPlan" => UiText.Get("Progress.ResultPlan"),
        "ResultPrepare" => UiText.Get("Progress.ResultPrepare"),
        "ResultRestore" => UiText.Get("Progress.ResultRestore"),
        "ResultFinalize" => UiText.Get("Progress.ResultFinalize"),
        _ => UiText.Get("Progress.ResultApplying")
    };
}
