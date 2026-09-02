using System.Text;

namespace SkyrimScan.Core.Scanning;

public sealed record Mo2ProfileStateChangeResult(
    string ProfilePath,
    bool GeneratedModEnabled,
    IReadOnlyList<string> EnabledPlugins,
    IReadOnlyList<string> DisabledPlugins,
    bool Changed);

/// <summary>
/// Applies explicitly confirmed mod/plugin state changes to one MO2 profile.
/// The writer keeps the scan model read-only and is only called by a user
/// action that has already confirmed the requested changes.
/// </summary>
public sealed class Mo2ProfileStateWriter
{
    public Mo2ProfileStateChangeResult Apply(
        string mo2Root,
        string profileName,
        string generatedModName,
        IReadOnlyList<string> generatedPluginNames,
        bool enableGeneratedMod,
        IReadOnlyList<string> sourcePluginNames,
        bool disableSourcePlugins)
    {
        if (string.IsNullOrWhiteSpace(mo2Root))
        {
            throw new ArgumentException("MO2ルートが指定されていません。", nameof(mo2Root));
        }

        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ArgumentException("MO2プロファイルが指定されていません。", nameof(profileName));
        }

        if (string.IsNullOrWhiteSpace(generatedModName))
        {
            throw new ArgumentException("生成MOD名が指定されていません。", nameof(generatedModName));
        }

        ArgumentNullException.ThrowIfNull(generatedPluginNames);
        ArgumentNullException.ThrowIfNull(sourcePluginNames);

        var root = Path.GetFullPath(mo2Root);
        var profilePath = Path.Combine(root, "profiles", profileName);
        if (!Directory.Exists(profilePath))
        {
            throw new DirectoryNotFoundException($"MO2プロファイルが見つかりません：{profilePath}");
        }

        var normalizedGeneratedPlugins = NormalizePluginNames(generatedPluginNames);
        var generatedPluginSet = normalizedGeneratedPlugins
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalizedSourcePlugins = NormalizePluginNames(sourcePluginNames)
            .Except(normalizedGeneratedPlugins, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var updates = new List<FileUpdate>();

        var modListPath = Path.Combine(profilePath, "modlist.txt");
        var modList = ReadLines(modListPath);
        var updatedModList = SetModState(modList, generatedModName, enableGeneratedMod);
        if (!LinesEqual(modList, updatedModList))
        {
            updates.Add(new FileUpdate(modListPath, updatedModList));
        }

        var loadOrderPath = Path.Combine(profilePath, "loadorder.txt");
        var loadOrder = ReadLines(loadOrderPath);
        var updatedLoadOrder = EnsurePluginLoadOrder(
            loadOrder,
            generatedModName,
            normalizedGeneratedPlugins);
        if (!LinesEqual(loadOrder, updatedLoadOrder))
        {
            updates.Add(new FileUpdate(loadOrderPath, updatedLoadOrder));
        }

        var pluginStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var pluginName in normalizedGeneratedPlugins)
        {
            pluginStates[pluginName] = enableGeneratedMod;
        }

        if (disableSourcePlugins)
        {
            foreach (var pluginName in normalizedSourcePlugins)
            {
                pluginStates[pluginName] = false;
            }
        }

        var pluginsPath = Path.Combine(profilePath, "plugins.txt");
        var plugins = ReadLines(pluginsPath);
        var updatedPlugins = SetPluginStates(
            plugins,
            generatedModName,
            generatedPluginSet,
            pluginStates);
        if (!LinesEqual(plugins, updatedPlugins))
        {
            updates.Add(new FileUpdate(pluginsPath, updatedPlugins));
        }

        var originalFiles = updates.ToDictionary(
            update => update.Path,
            update => File.Exists(update.Path) ? File.ReadAllBytes(update.Path) : null,
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var update in updates)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(update.Path)!);
                File.WriteAllLines(update.Path, update.Lines, new UTF8Encoding(false));
            }
        }
        catch
        {
            RestoreFiles(originalFiles);
            throw;
        }

        return new Mo2ProfileStateChangeResult(
            profilePath,
            enableGeneratedMod,
            pluginStates
                .Where(pair => pair.Value)
                .Select(pair => pair.Key)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            pluginStates
                .Where(pair => !pair.Value)
                .Select(pair => pair.Key)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            updates.Count > 0);
    }

    private static IReadOnlyList<string> NormalizePluginNames(IEnumerable<string> names) => names
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => Path.GetFileName(name.Trim()))
        .Where(name => name.Length > 0 && IsPluginName(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static List<string> ReadLines(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>();

    private static List<string> SetModState(
        IReadOnlyList<string> lines,
        string modName,
        bool enabled)
    {
        var result = lines
            .Where(line => !IsModEntry(line, modName))
            .ToList();
        var generatedModLine = $"{(enabled ? '+' : '-')}{modName}";
        var firstModIndex = result.FindIndex(IsAnyModEntry);
        if (firstModIndex < 0)
        {
            result.Add(generatedModLine);
        }
        else
        {
            // MO2 stores modlist.txt in reverse left-pane order.  The first
            // mod entry is therefore the bottom/highest-priority mod.
            result.Insert(firstModIndex, generatedModLine);
        }

        return result;
    }

    private static List<string> EnsurePluginLoadOrder(
        IReadOnlyList<string> lines,
        string generatedModName,
        IReadOnlyList<string> pluginNames)
    {
        var result = lines
            .Where(line =>
            {
                var pluginName = GetPluginName(line);
                return pluginName is null ||
                       !IsGeneratedPluginName(pluginName, generatedModName);
            })
            .ToList();
        result.AddRange(pluginNames);
        return result;
    }

    private static List<string> SetPluginStates(
        IReadOnlyList<string> lines,
        string generatedModName,
        IReadOnlySet<string> generatedPluginNames,
        IReadOnlyDictionary<string, bool> states)
    {
        var result = lines.ToList();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < result.Count; index++)
        {
            var line = result[index];
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var enabled = trimmed[0] == '*';
            var pluginName = enabled ? trimmed[1..].Trim() : trimmed;
            if (IsGeneratedPluginName(pluginName, generatedModName) &&
                !generatedPluginNames.Contains(pluginName))
            {
                result.RemoveAt(index);
                index--;
                continue;
            }

            if (!states.TryGetValue(pluginName, out var desiredEnabled))
            {
                continue;
            }

            var indentationLength = line.Length - line.TrimStart().Length;
            var indentation = line[..indentationLength];
            result[index] = indentation + (desiredEnabled ? "*" : string.Empty) + pluginName;
            found.Add(pluginName);
        }

        foreach (var pair in states)
        {
            if (!found.Contains(pair.Key))
            {
                result.Add((pair.Value ? "*" : string.Empty) + pair.Key);
            }
        }

        return result;
    }

    private static bool IsGeneratedPluginName(
        string pluginName,
        string generatedModName)
    {
        var normalizedPluginName = Path.GetFileName(pluginName.Trim());
        if (!IsPluginName(normalizedPluginName))
        {
            return false;
        }

        var generatedBaseName = Path.GetFileNameWithoutExtension(
            generatedModName.Trim());
        var pluginBaseName = Path.GetFileNameWithoutExtension(normalizedPluginName);
        if (pluginBaseName.Equals(
                generatedBaseName,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var splitPrefix = generatedBaseName + " - ";
        if (!pluginBaseName.StartsWith(
                splitPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = pluginBaseName[splitPrefix.Length..];
        return suffix.Length > 0 && suffix.All(char.IsDigit);
    }

    private static bool IsModEntry(string line, string modName)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 2 &&
               trimmed[0] is ('+' or '-') &&
               trimmed[1..].Trim().Equals(modName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnyModEntry(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 2 && trimmed[0] is ('+' or '-');
    }

    private static string? GetPluginName(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            return null;
        }

        if (trimmed[0] == '*')
        {
            trimmed = trimmed[1..].Trim();
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool IsPluginName(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".esl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LinesEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.SequenceEqual(right, StringComparer.Ordinal);

    private static void RestoreFiles(
        IReadOnlyDictionary<string, byte[]?> originalFiles)
    {
        foreach (var pair in originalFiles)
        {
            if (pair.Value is null)
            {
                if (File.Exists(pair.Key))
                {
                    File.Delete(pair.Key);
                }

                continue;
            }

            File.WriteAllBytes(pair.Key, pair.Value);
        }
    }

    private sealed record FileUpdate(string Path, IReadOnlyList<string> Lines);
}
