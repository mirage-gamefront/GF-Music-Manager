using GfMusicManager.Core.Analysis;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Planning;

/// <summary>
/// Selects plugins that belong to the MO2 mods supplying adopted music assets.
/// Destination records from unrelated mods must not be treated as source ESPs.
/// </summary>
public static class MusicSourcePluginSelector
{
    public static IReadOnlyList<string> Select(
        IReadOnlyList<MusicSettingSource> sourceSettings,
        IReadOnlySet<string> sourceModNames)
    {
        ArgumentNullException.ThrowIfNull(sourceSettings);
        ArgumentNullException.ThrowIfNull(sourceModNames);

        return sourceSettings
            .SelectMany(setting => new[]
            {
                setting.Record.Plugin,
                setting.MusicTypeRecord.Plugin
            })
            .Where(plugin => sourceModNames.Contains(plugin.ModName))
            .Where(IsPluginFile)
            .Select(plugin => plugin.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsPluginFile(PluginSource plugin)
    {
        var extension = Path.GetExtension(plugin.Name);
        return !plugin.ModName.Equals("Game Data", StringComparison.OrdinalIgnoreCase) &&
               (extension.Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".esl", StringComparison.OrdinalIgnoreCase));
    }
}
