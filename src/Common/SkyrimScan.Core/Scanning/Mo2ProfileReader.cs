using SkyrimScan.Core.Models;

namespace SkyrimScan.Core.Scanning;

public sealed class Mo2ProfileReader
{
    public IReadOnlyList<string> ListProfiles(string mo2Root)
    {
        var root = Path.GetFullPath(mo2Root);
        var profilesPath = Path.Combine(root, "profiles");
        if (!Directory.Exists(profilesPath))
        {
            throw new DirectoryNotFoundException($"MO2 profiles folder was not found: {profilesPath}");
        }

        return Directory
            .EnumerateDirectories(profilesPath)
            .Where(path => File.Exists(Path.Combine(path, "modlist.txt")))
            .Select(path => Path.GetFileName(path))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Mo2ProfileSnapshot Read(string mo2Root, string? profileName = null)
    {
        var root = Path.GetFullPath(mo2Root);
        var profilesPath = Path.Combine(root, "profiles");
        if (!Directory.Exists(profilesPath))
        {
            throw new DirectoryNotFoundException($"MO2 profiles folder was not found: {profilesPath}");
        }

        var profilePath = ResolveProfilePath(profilesPath, profileName);
        var resolvedProfileName = Path.GetFileName(profilePath);
        var modListPath = Path.Combine(profilePath, "modlist.txt");
        var loadOrderPath = Path.Combine(profilePath, "loadorder.txt");
        var pluginsPath = Path.Combine(profilePath, "plugins.txt");

        var modOrder = new List<string>();
        var modEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var modPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(modListPath))
        {
            var entries = new List<string>();
            foreach (var rawLine in File.ReadLines(modListPath))
            {
                var line = rawLine.Trim();
                if (line.Length < 2 || line.StartsWith('#') || line[0] is not ('+' or '-'))
                {
                    continue;
                }

                var name = line[1..].Trim();
                if (name.Length == 0 || name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                modOrder.Add(name);
                modEnabled[name] = line[0] == '+';
                entries.Add(name);
            }

            for (var index = 0; index < entries.Count; index++)
            {
                // MO2 stores modlist.txt in the reverse order of the left pane:
                // the first mod entry is the highest-priority (bottom) mod.
                modPriority[entries[index]] = entries.Count - 1 - index;
            }
        }

        var pluginLoadOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(loadOrderPath))
        {
            var order = 0;
            foreach (var rawLine in File.ReadLines(loadOrderPath))
            {
                var name = rawLine.Trim();
                if (name.Length == 0 || name.StartsWith('#'))
                {
                    continue;
                }

                pluginLoadOrder[name] = order++;
            }
        }

        var pluginEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(pluginsPath))
        {
            foreach (var rawLine in File.ReadLines(pluginsPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var enabled = line[0] == '*';
                var name = enabled ? line[1..].Trim() : line;
                if (name.Length > 0)
                {
                    pluginEnabled[name] = enabled;
                }
            }
        }

        return new Mo2ProfileSnapshot(
            root,
            resolvedProfileName,
            profilePath,
            modOrder,
            modEnabled,
            modPriority,
            pluginLoadOrder,
            pluginEnabled,
            File.Exists(modListPath),
            File.Exists(loadOrderPath),
            File.Exists(pluginsPath),
            ReadGamePath(root));
    }

    private static string? ReadGamePath(string mo2Root)
    {
        var configPath = Path.Combine(mo2Root, "ModOrganizer.ini");
        if (!File.Exists(configPath))
        {
            return null;
        }

        foreach (var rawLine in File.ReadLines(configPath))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("gamePath=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["gamePath=".Length..].Trim();
            const string byteArrayPrefix = "@ByteArray(";
            if (value.StartsWith(byteArrayPrefix, StringComparison.Ordinal) && value.EndsWith(')'))
            {
                value = value[byteArrayPrefix.Length..^1];
            }

            value = value
                .Replace(@"\\", @"\")
                .Replace('/', Path.DirectorySeparatorChar);
            return string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
        }

        return null;
    }

    private static string ResolveProfilePath(string profilesPath, string? profileName)
    {
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            var requested = Path.Combine(profilesPath, profileName);
            if (!Directory.Exists(requested))
            {
                throw new DirectoryNotFoundException($"MO2 profile was not found: {requested}");
            }

            return requested;
        }

        var candidates = Directory
            .EnumerateDirectories(profilesPath)
            .Where(path => File.Exists(Path.Combine(path, "modlist.txt")))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new FileNotFoundException($"No MO2 profile with modlist.txt was found: {profilesPath}");
        }

        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"Multiple MO2 profiles were found. Select a profile explicitly: {string.Join(", ", candidates.Select(Path.GetFileName))}");
        }

        return candidates[0];
    }
}
