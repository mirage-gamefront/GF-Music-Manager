using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Analysis;

public sealed record FantasySoundtrackProjectRepair(
    string PluginName,
    string RecordFormKey,
    string? RecordEditorId,
    string OriginalAudioPath,
    string RepairedAudioPath);

public sealed record FantasySoundtrackProjectRepairReport(
    bool IsDetected,
    IReadOnlyList<FantasySoundtrackProjectRepair> AudioPathRepairs,
    IReadOnlyList<string> UnresolvedAudioRepairs)
{
    public static FantasySoundtrackProjectRepairReport Empty { get; } =
        new(
            false,
            Array.Empty<FantasySoundtrackProjectRepair>(),
            Array.Empty<string>());

    public bool HasAutomaticFixes => AudioPathRepairs.Count > 0;
}

internal static class FantasySoundtrackProjectRepairCatalog
{
    private const string SourcePathPrefix = "music\\fantasy_soundtrack\\town\\";
    private const string RepairedPathPrefix = "music\\fantasy_soundtrack\\towns\\";

    public static FantasySoundtrackProjectRepairReport Analyze(
        IReadOnlyList<PluginRecordSource> records,
        IReadOnlyList<AssetSource> assets,
        IReadOnlyList<PluginSource>? plugins = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(assets);

        var fspPlugins = records
            .Select(record => record.Plugin)
            .Concat(plugins ?? Array.Empty<PluginSource>())
            .Where(IsFantasySoundtrackProjectPlugin)
            .ToArray();
        var fspPluginNames = fspPlugins
            .Select(plugin => plugin.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fspModNames = records
            .Select(record => record.Plugin.ModName)
            .Concat(plugins?.Select(plugin => plugin.ModName) ?? Array.Empty<string>())
            .Where(IsFantasySoundtrackProjectModName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fspRecords = records
            .Where(record =>
                !record.IsDeleted &&
                IsFantasySoundtrackProjectPlugin(record.Plugin) &&
                record.RecordType.Equals("MusicTrack", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var fspAssets = assets
            .Where(asset =>
                fspModNames.Contains(asset.ModName) ||
                IsFantasySoundtrackProjectModName(asset.ModName))
            .ToArray();

        var isDetected = fspPluginNames.Count > 0 || fspAssets.Length > 0;
        if (!isDetected)
        {
            return FantasySoundtrackProjectRepairReport.Empty;
        }

        var repairs = new List<FantasySoundtrackProjectRepair>();
        var unresolved = new List<string>();
        foreach (var record in fspRecords)
        {
            foreach (var sourceAsset in record.Assets
                         .Where(asset => asset.FieldName is "TrackFilename" or "FinaleFilename"))
            {
                if (!TryCreateRepairedPath(sourceAsset.VirtualPath, out var repairedPath))
                {
                    continue;
                }

                var repairedAsset = fspAssets.FirstOrDefault(asset =>
                    NormalizeAssetPath(asset.VirtualPath).Equals(
                        repairedPath,
                        StringComparison.OrdinalIgnoreCase));
                if (repairedAsset is null)
                {
                    unresolved.Add(
                        $"{record.Plugin.Name}:{record.EditorId ?? record.FormKey} -> {repairedPath}");
                    continue;
                }

                repairs.Add(new FantasySoundtrackProjectRepair(
                    record.Plugin.Name,
                    record.FormKey,
                    record.EditorId,
                    sourceAsset.VirtualPath,
                    repairedAsset.VirtualPath));
            }
        }

        return new FantasySoundtrackProjectRepairReport(
            true,
            repairs
                .DistinctBy(
                    repair => string.Join(
                        "\u001f",
                        repair.RecordFormKey,
                        repair.OriginalAudioPath,
                        repair.RepairedAudioPath),
                    StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            unresolved.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool IsFantasySoundtrackProjectPlugin(PluginSource plugin) =>
        IsFantasySoundtrackProjectPluginName(plugin.Name) ||
        IsFantasySoundtrackProjectModName(plugin.ModName);

    private static bool IsFantasySoundtrackProjectPluginName(string? pluginName)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            return false;
        }

        var normalized = NormalizeName(Path.GetFileNameWithoutExtension(pluginName));
        return normalized.Contains("FantasySoundtrackProject", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFantasySoundtrackProjectModName(string? modName)
    {
        if (string.IsNullOrWhiteSpace(modName))
        {
            return false;
        }

        return NormalizeName(modName)
            .Contains("FantasySoundtrackProject", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeName(string value) =>
        new(value.Where(char.IsLetterOrDigit).ToArray());

    private static bool TryCreateRepairedPath(string path, out string repairedPath)
    {
        var normalized = NormalizeAssetPath(path);
        if (!normalized.StartsWith(SourcePathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            repairedPath = string.Empty;
            return false;
        }

        repairedPath = RepairedPathPrefix + normalized[SourcePathPrefix.Length..];
        return true;
    }

    private static string NormalizeAssetPath(string path) =>
        path
            .Replace('/', '\\')
            .TrimStart('\\')
            .Replace("data\\", string.Empty, StringComparison.OrdinalIgnoreCase);
}
