using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Analysis;

public sealed record AdditionalMusicProjectRepair(
    string IssueId,
    string Description,
    string PluginName,
    string RecordFormKey,
    string? RecordEditorId,
    string OriginalAudioPath,
    string RepairedAudioPath);

public sealed record AdditionalMusicProjectRepairReport(
    bool IsDetected,
    IReadOnlyList<AdditionalMusicProjectRepair> AudioPathRepairs,
    int CombatTrackCount,
    IReadOnlyList<string> UnresolvedAudioRepairs)
{
    public static AdditionalMusicProjectRepairReport Empty { get; } =
        new(
            false,
            Array.Empty<AdditionalMusicProjectRepair>(),
            0,
            Array.Empty<string>());

    public bool HasAutomaticFixes =>
        AudioPathRepairs.Count > 0 || CombatTrackCount > 0;
}

internal static class AdditionalMusicProjectRepairCatalog
{
    private static readonly IReadOnlyList<PathRepairDefinition> PathRepairs =
        new[]
        {
            new PathRepairDefinition(
                "AMP_Heroics_Path",
                "「Definitely A Time For Heroics」の音源パス誤記",
                new[] { "ADMPIVCombat02", "ADMPIVCombatBoss03" },
                new[] { "ADMP Definitely Time For Heroïcs.xwm" },
                "ADMP Definitely A Time For Heroics.xwm"),
            new PathRepairDefinition(
                "AMP_AweOfGodFearingMen_Path",
                "「The Awe Of God Fearing Men」の音源パス不一致",
                new[] { "ADMPIVExploreNight04" },
                new[] { "ADMP The Awe Of God Fearing Men.xwm" },
                "ADMP The Work Of God Fearing Men.xwm")
        };

    public static AdditionalMusicProjectRepairReport Analyze(
        IReadOnlyList<PluginRecordSource> records,
        IReadOnlyList<AssetSource> assets,
        IReadOnlyList<PluginSource>? plugins = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(assets);

        var ampPluginNames = records
            .Select(record => record.Plugin)
            .Concat(plugins ?? Array.Empty<PluginSource>())
            .Where(IsAdditionalMusicProjectPlugin)
            .Select(plugin => plugin.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ampModNames = records
            .Select(record => record.Plugin.ModName)
            .Concat(plugins?.Select(plugin => plugin.ModName) ?? Array.Empty<string>())
            .Where(IsAdditionalMusicProjectModName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ampRecords = records
            .Where(record =>
                !record.IsDeleted &&
                IsAdditionalMusicProjectPlugin(record.Plugin) &&
                record.RecordType.Equals("MusicTrack", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var ampAssets = assets
            .Where(asset =>
                ampModNames.Contains(asset.ModName) ||
                IsAdditionalMusicProjectModName(asset.ModName))
            .ToArray();
        var isDetected = ampPluginNames.Count > 0 || ampAssets.Length > 0;
        if (!isDetected)
        {
            return AdditionalMusicProjectRepairReport.Empty;
        }

        var repairs = new List<AdditionalMusicProjectRepair>();
        var unresolved = new List<string>();
        foreach (var definition in PathRepairs)
        {
            foreach (var record in ampRecords.Where(record =>
                         definition.RecordEditorIds.Contains(
                             record.EditorId ?? string.Empty,
                             StringComparer.OrdinalIgnoreCase)))
            {
                var sourceAssets = record.Assets
                    .Where(asset =>
                        asset.FieldName is "TrackFilename" or "FinaleFilename" &&
                        definition.OriginalFileNames.Any(fileName =>
                            FileNameEquals(asset.VirtualPath, fileName)))
                    .ToArray();
                var repairedAssets = ampAssets
                    .Where(asset => FileNameEquals(asset.VirtualPath, definition.RepairedFileName))
                    .GroupBy(asset => asset.VirtualPath, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();

                foreach (var sourceAsset in sourceAssets)
                {
                    if (repairedAssets.Length == 0)
                    {
                        unresolved.Add(
                            $"{record.Plugin.Name}:{record.EditorId ?? record.FormKey} -> " +
                            definition.RepairedFileName);
                        continue;
                    }

                    foreach (var repairedAsset in repairedAssets)
                    {
                        if (FileNameEquals(sourceAsset.VirtualPath, repairedAsset.VirtualPath))
                        {
                            continue;
                        }

                        repairs.Add(new AdditionalMusicProjectRepair(
                            definition.IssueId,
                            definition.Description,
                            record.Plugin.Name,
                            record.FormKey,
                            record.EditorId,
                            sourceAsset.VirtualPath,
                            repairedAsset.VirtualPath));
                    }
                }
            }
        }

        var combatTrackCount = ampRecords.Count(record =>
            record.EditorId?.StartsWith("ADMPIVCombat", StringComparison.OrdinalIgnoreCase) == true);
        return new AdditionalMusicProjectRepairReport(
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
            combatTrackCount,
            unresolved.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool IsAdditionalMusicProjectPlugin(PluginSource plugin) =>
        IsAdditionalMusicProjectPluginName(plugin.Name) ||
        IsAdditionalMusicProjectModName(plugin.ModName);

    private static bool IsAdditionalMusicProjectPluginName(string? pluginName)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
        {
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(pluginName);
        return baseName.StartsWith(
            "AdditionalMusicProject",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdditionalMusicProjectModName(string? modName)
    {
        if (string.IsNullOrWhiteSpace(modName))
        {
            return false;
        }

        var normalized = new string(modName
            .Where(char.IsLetterOrDigit)
            .ToArray());
        return normalized.Contains(
            "AdditionalMusicProject",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool FileNameEquals(string left, string right) =>
        GetFileName(left).Equals(GetFileName(right), StringComparison.OrdinalIgnoreCase);

    private static string GetFileName(string path) =>
        path.Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? path;

    private sealed record PathRepairDefinition(
        string IssueId,
        string Description,
        IReadOnlyList<string> RecordEditorIds,
        IReadOnlyList<string> OriginalFileNames,
        string RepairedFileName);
}
