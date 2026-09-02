using SkyrimScan.Core.Archives;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Plugins;

namespace SkyrimScan.Core.Scanning;

public sealed class Mo2Scanner
{
    private readonly Mo2ProfileReader _profileReader = new();
    private readonly BsaArchiveReader _archiveReader = new();
    private readonly PluginRecordScanner _pluginRecordScanner = new();

    public IReadOnlyList<string> GetProfileNames(string mo2Root) => _profileReader.ListProfiles(mo2Root);

    public ScanResult Scan(
        ScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = Path.GetFullPath(options.Mo2Root);
        var issues = new List<ScanIssue>();
        var profile = _profileReader.Read(root, options.ProfileName);
        AddProfileWarnings(profile, issues);

        var modsRoot = Path.Combine(root, "mods");
        if (!Directory.Exists(modsRoot))
        {
            throw new DirectoryNotFoundException($"MO2 mods folder was not found: {modsRoot}");
        }

        var modNames = GetModNames(profile, modsRoot, options.IncludeDisabledMods, issues);
        var mods = new List<ModSource>();
        var plugins = new List<PluginSource>();
        var assets = new List<AssetSource>();

        progress?.Report(new ScanProgress(
            ScanIssueSeverity.Info,
            "MOD",
            "MOD scan starting",
            0,
            modNames.Count));

        for (var index = 0; index < modNames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modName = modNames[index];
            var modPath = Path.Combine(modsRoot, modName);
            if (options.ExcludedModNames.Contains(modName))
            {
                issues.Add(new ScanIssue(
                    ScanIssueSeverity.Info,
                    "MOD",
                    modPath,
                    "The caller excluded this mod from the scan.",
                    modName));
                continue;
            }

            var enabled = !profile.HasModList || profile.IsModEnabled(modName);
            if (!enabled && !options.IncludeDisabledMods)
            {
                continue;
            }

            progress?.Report(new ScanProgress(
                ScanIssueSeverity.Info,
                "MOD",
                $"Scanning {modName}",
                index + 1,
                modNames.Count,
                modName,
                modPath));

            if (!Directory.Exists(modPath))
            {
                issues.Add(new ScanIssue(
                    ScanIssueSeverity.Warning,
                    "MOD",
                    modPath,
                    "The mod listed by the profile does not exist on disk."));
                continue;
            }

            var modPlugins = EnumeratePluginPaths(modPath, cancellationToken).ToArray();
            var modArchives = Directory
                .EnumerateFiles(modPath, "*.bsa", SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var priority = profile.GetModPriority(modName);
            if (priority < 0)
            {
                priority = index;
            }

            var mod = new ModSource(modName, modPath, enabled, priority, modPlugins, modArchives);
            mods.Add(mod);

            foreach (var pluginPath in modPlugins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pluginName = Path.GetFileName(pluginPath);
                plugins.Add(new PluginSource(
                    pluginName,
                    pluginPath,
                    mod.Name,
                    mod.Path,
                    mod.Enabled,
                    mod.Enabled && profile.IsPluginEnabled(pluginName),
                    profile.GetPluginLoadOrder(pluginName),
                    mod.Priority));
            }

            if (options.ScanLooseAssets)
            {
                ScanLooseAssets(mod, options, assets, issues, progress, cancellationToken);
            }

            if (options.ScanArchives)
            {
                ScanBsaAssets(mod, options, assets, issues, progress, cancellationToken);
            }
        }

        IReadOnlyList<PluginSource> recordPlugins = options.ReadPluginRecords
            ? plugins
                .Concat(ReadGameDataPlugins(profile, plugins, issues))
                .ToArray()
            : plugins;
        if (options.ReadPluginRecords)
        {
            progress?.Report(new ScanProgress(
                ScanIssueSeverity.Info,
                "Plugin",
                "Plugin record scan starting",
                0,
                recordPlugins.Count));
        }
        var records = options.ReadPluginRecords
            ? ReadRecords(
                recordPlugins,
                issues,
                progress,
                cancellationToken,
                options.IncludedRecordTypes,
                options.RetainOnlyMusicAssignments)
            : Array.Empty<PluginRecordSource>();
        records = MarkWinners(records);

        var resolvedAssets = MarkVfsWinners(assets, mods);
        return new ScanResult(profile, mods, plugins, records, resolvedAssets, issues);
    }

    private static IReadOnlyList<AssetSource> MarkVfsWinners(
        IReadOnlyList<AssetSource> assets,
        IReadOnlyList<ModSource> mods)
    {
        var priorities = mods.ToDictionary(
            mod => mod.Name,
            mod => mod.Priority,
            StringComparer.OrdinalIgnoreCase);
        var winners = assets
            .GroupBy(asset => NormalizePath(asset.VirtualPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .Where(asset => asset.ModEnabled)
                .OrderByDescending(asset => priorities.TryGetValue(asset.ModName, out var priority)
                    ? priority
                    : -1)
                .ThenBy(asset => asset.SourceKind == AssetSourceKind.Loose ? 0 : 1)
                .ThenBy(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(asset => asset.ArchiveEntryPath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault())
            .Where(asset => asset is not null)
            .Select(asset => CreateAssetIdentity(asset!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return assets
            .Select(asset => asset with
            {
                IsVfsWinner = winners.Contains(CreateAssetIdentity(asset))
            })
            .ToArray();
    }

    private static string CreateAssetIdentity(AssetSource asset) => string.Join(
        "\u001f",
        NormalizePath(asset.VirtualPath),
        asset.ModName,
        asset.SourceKind,
        asset.SourcePath,
        asset.ArchiveEntryPath ?? string.Empty);

    private static IReadOnlyList<string> GetModNames(
        Mo2ProfileSnapshot profile,
        string modsRoot,
        bool includeDisabled,
        ICollection<ScanIssue> issues)
    {
        if (!profile.HasModList)
        {
            issues.Add(new ScanIssue(
                ScanIssueSeverity.Warning,
                "MO2",
                profile.ProfilePath,
                "modlist.txt was not found; all mod folders are treated as enabled."));
            return Directory
                .EnumerateDirectories(modsRoot)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return profile.ModOrder
            .Where(name => includeDisabled || profile.IsModEnabled(name))
            .ToArray();
    }

    private static IEnumerable<string> EnumeratePluginPaths(
        string modPath,
        CancellationToken cancellationToken)
    {
        foreach (var extension in new[] { "*.esm", "*.esp", "*.esl" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFiles(modPath, extension, SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return path;
            }
        }
    }

    private void ScanLooseAssets(
        ModSource mod,
        ScanOptions options,
        ICollection<AssetSource> assets,
        ICollection<ScanIssue> issues,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(mod.Path, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = NormalizePath(Path.GetRelativePath(mod.Path, path));
                if (!IsMusicAsset(relativePath, Path.GetExtension(path), options.AssetExtensions))
                {
                    continue;
                }

                var length = new FileInfo(path).Length;
                assets.Add(new AssetSource(
                    relativePath,
                    AssetSourceKind.Loose,
                    mod.Name,
                    mod.Path,
                    mod.Enabled,
                    path,
                    null,
                    length));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            issues.Add(new ScanIssue(
                ScanIssueSeverity.Warning,
                "LooseAsset",
                mod.Path,
                "Loose asset enumeration failed; other scan results were retained.",
                exception.ToString()));
            progress?.Report(new ScanProgress(
                ScanIssueSeverity.Warning,
                "LooseAsset",
                $"Could not enumerate loose assets: {mod.Name}",
                ModName: mod.Name,
                SourcePath: mod.Path));
        }
    }

    private void ScanBsaAssets(
        ModSource mod,
        ScanOptions options,
        ICollection<AssetSource> assets,
        ICollection<ScanIssue> issues,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var archivePath in mod.ArchivePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var archive = _archiveReader.ReadIndex(archivePath);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsMusicAsset(entry.VirtualPath, Path.GetExtension(entry.VirtualPath), options.AssetExtensions))
                    {
                        continue;
                    }

                    assets.Add(new AssetSource(
                        entry.VirtualPath,
                        AssetSourceKind.Bsa,
                        mod.Name,
                        mod.Path,
                        mod.Enabled,
                        archivePath,
                        entry.VirtualPath,
                        entry.PackedSize));
                }

                progress?.Report(new ScanProgress(
                    ScanIssueSeverity.Info,
                    "BSA",
                    $"Indexed {Path.GetFileName(archivePath)}",
                    ModName: mod.Name,
                    SourcePath: archivePath));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                issues.Add(new ScanIssue(
                    ScanIssueSeverity.Warning,
                    "BSA",
                    archivePath,
                    "BSA indexing failed; loose assets and other archives were retained.",
                    exception.ToString()));
                progress?.Report(new ScanProgress(
                    ScanIssueSeverity.Warning,
                    "BSA",
                    $"Could not index {Path.GetFileName(archivePath)}",
                    ModName: mod.Name,
                    SourcePath: archivePath));
            }
        }
    }

    private IReadOnlyList<PluginRecordSource> ReadRecords(
        IReadOnlyList<PluginSource> plugins,
        ICollection<ScanIssue> issues,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? includedRecordTypes,
        bool retainOnlyMusicAssignments)
    {
        var records = new List<PluginRecordSource>();
        for (var index = 0; index < plugins.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plugin = plugins[index];
            progress?.Report(new ScanProgress(
                ScanIssueSeverity.Info,
                "Plugin",
                $"Reading {plugin.Name}",
                index + 1,
                plugins.Count,
                plugin.ModName,
                plugin.Path,
                plugin.Name));
            try
            {
                records.AddRange(_pluginRecordScanner.Read(
                    plugin,
                    cancellationToken,
                    issues,
                    includedRecordTypes,
                    retainOnlyMusicAssignments));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                issues.Add(new ScanIssue(
                    ScanIssueSeverity.Warning,
                    "Plugin",
                    plugin.Path,
                    "Plugin record reading failed; other plugins were retained.",
                    exception.ToString()));
            }
        }

        return records;
    }

    private static IReadOnlyList<PluginSource> ReadGameDataPlugins(
        Mo2ProfileSnapshot profile,
        IReadOnlyList<PluginSource> modPlugins,
        ICollection<ScanIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(profile.GamePath))
        {
            return Array.Empty<PluginSource>();
        }

        var dataPath = Path.Combine(profile.GamePath, "Data");
        if (!Directory.Exists(dataPath))
        {
            issues.Add(new ScanIssue(
                ScanIssueSeverity.Warning,
                "GameData",
                dataPath,
                "The game Data folder was not found; official plugin records were not included."));
            return Array.Empty<PluginSource>();
        }

        var modPluginNames = modPlugins
            .Select(plugin => plugin.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var configuredNames = profile.PluginLoadOrder.Keys
            .Concat(profile.PluginEnabled.Keys)
            .Where(name => IsPluginFile(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pluginNames = configuredNames.Length > 0
            ? configuredNames
            : Directory
                .EnumerateFiles(dataPath, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) && IsPluginFile(name!))
                .Select(name => name!)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return pluginNames
            .Where(name => !modPluginNames.Contains(name))
            .Select(name => new
            {
                Name = name,
                Path = Path.Combine(dataPath, name)
            })
            .Where(plugin => File.Exists(plugin.Path))
            .Select(plugin => new PluginSource(
                plugin.Name,
                plugin.Path,
                "Game Data",
                dataPath,
                true,
                profile.IsPluginEnabled(plugin.Name),
                profile.GetPluginLoadOrder(plugin.Name),
                int.MinValue))
            .ToArray();
    }

    private static bool IsPluginFile(string name) =>
        Path.GetExtension(name).Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(name).Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(name).Equals(".esl", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<PluginRecordSource> MarkWinners(
        IReadOnlyList<PluginRecordSource> records)
    {
        return records
            .GroupBy(record => record.FormKey, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group =>
            {
                var ordered = group
                    .OrderByDescending(record => record.Plugin.Enabled)
                    .ThenByDescending(record => record.Plugin.LoadOrderIndex)
                    .ThenByDescending(record => record.Plugin.ModPriority)
                    .ThenByDescending(record => record.Plugin.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return ordered.Select((record, index) => record with { IsWinner = index == 0 });
            })
            .OrderBy(record => record.Plugin.LoadOrderIndex)
            .ThenBy(record => record.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsMusicAsset(
        string virtualPath,
        string extension,
        IReadOnlySet<string> allowedExtensions)
    {
        if (!allowedExtensions.Contains(extension))
        {
            return false;
        }

        return virtualPath.StartsWith("music\\", StringComparison.OrdinalIgnoreCase) ||
               virtualPath.StartsWith("sound\\music\\", StringComparison.OrdinalIgnoreCase) ||
               virtualPath.Contains("\\music\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => path
        .Replace('/', '\\')
        .TrimStart('\\');

    private static void AddProfileWarnings(
        Mo2ProfileSnapshot profile,
        ICollection<ScanIssue> issues)
    {
        if (!profile.HasLoadOrder)
        {
            issues.Add(new ScanIssue(
                ScanIssueSeverity.Warning,
                "MO2",
                profile.ProfilePath,
                "loadorder.txt was not found; plugin conflict order may be incomplete."));
        }

        if (!profile.HasPluginList)
        {
            issues.Add(new ScanIssue(
                ScanIssueSeverity.Warning,
                "MO2",
                profile.ProfilePath,
                "plugins.txt was not found; plugin enablement may be incomplete."));
        }
    }

}
