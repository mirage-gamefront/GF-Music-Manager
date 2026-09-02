using System.Text.Json;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;
using Mutagen.Bethesda.Plugins;
using SkyrimScan.Core.Archives;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Plugins;

namespace GfMusicManager.Core.Generation;

public sealed record ExistingMusicProductLoadResult(
    string OutputModDirectory,
    bool IsDetected,
    bool IsComplete,
    MusicGenerationManifest? Manifest,
    IReadOnlyList<string> Warnings)
{
    public bool CanRestore => Manifest is not null && IsComplete;

    public static ExistingMusicProductLoadResult NotDetected(string outputModDirectory) =>
        new(outputModDirectory, false, false, null, Array.Empty<string>());
}

public sealed record MusicGenerationPlanRestoreResult(
    bool IsRestored,
    bool HasCompleteEntryState,
    bool UsedLegacyTrackFallback,
    int RestoredEntryCount,
    int MissingEntryCount,
    int UnresolvedDestinationCount,
    IReadOnlySet<string> RestoredAssetKeys,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyDictionary<string, int> MissingEntriesByMod { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public static MusicGenerationPlanRestoreResult Empty { get; } =
        new(
            false,
            false,
            false,
            0,
            0,
            0,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<string>());
}

/// <summary>
/// Reads the fixed GF Music Product directory without treating it as an
/// ordinary source MOD.  The loader validates the files that the manifest
/// says were generated and reports discrepancies instead of silently fixing
/// them.
/// </summary>
public sealed class ExistingMusicProductLoader
{
    public const string GeneratedModName = "GF Music Product";
    public const string ManifestFileName = "GFMusicProduct.json";

    private readonly PluginRecordScanner _pluginRecordScanner = new();
    private readonly BsaArchiveReader _archiveReader = new();

    public ExistingMusicProductLoadResult Load(string mo2Root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mo2Root);
        var outputDirectory = Path.GetFullPath(
            Path.Combine(mo2Root, "mods", GeneratedModName));
        return LoadDirectory(outputDirectory);
    }

    public ExistingMusicProductLoadResult LoadDirectory(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(fullOutputDirectory))
        {
            return ExistingMusicProductLoadResult.NotDetected(fullOutputDirectory);
        }

        var warnings = new List<string>();
        var manifestPath = Path.Combine(fullOutputDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            warnings.Add($"既存のGF Music Productに{ManifestFileName}がありません。再編集用の設定を復元できません。");
            return new ExistingMusicProductLoadResult(
                fullOutputDirectory,
                true,
                false,
                null,
                warnings);
        }

        MusicGenerationManifest? manifest;
        try
        {
            var json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize<MusicGenerationManifest>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception exception)
        {
            warnings.Add($"GFMusicProduct.jsonを読み込めません：{exception.Message}");
            return new ExistingMusicProductLoadResult(
                fullOutputDirectory,
                true,
                false,
                null,
                warnings);
        }

        if (manifest is null)
        {
            warnings.Add("GFMusicProduct.jsonの内容が空です。");
            return new ExistingMusicProductLoadResult(
                fullOutputDirectory,
                true,
                false,
                null,
                warnings);
        }

        if (manifest.SchemaVersion is < 4 or > 5)
        {
            warnings.Add($"GFMusicProduct.jsonのスキーマバージョンに対応していません：{manifest.SchemaVersion}");
        }

        var normalizedManifest = manifest with
        {
            PlanEntries = manifest.PlanEntries ?? Array.Empty<MusicGenerationPlanEntryOutput>()
        };
        var requiredFileFailure = false;

        if (string.IsNullOrWhiteSpace(normalizedManifest.MtdFileName))
        {
            warnings.Add("生成MTDファイル名が記録されていません。");
            requiredFileFailure = true;
        }
        else if (!File.Exists(CombineRelativePath(
                     fullOutputDirectory,
                     normalizedManifest.MtdFileName)))
        {
            warnings.Add($"生成MTDファイルが見つかりません：{normalizedManifest.MtdFileName}");
            requiredFileFailure = true;
        }
        else
        {
            ValidateMtd(
                normalizedManifest,
                CombineRelativePath(fullOutputDirectory, normalizedManifest.MtdFileName),
                warnings);
        }

        if (normalizedManifest.Plugins.Count == 0)
        {
            warnings.Add("生成ESPが記録されていません。");
            requiredFileFailure = true;
        }

        var generatedRecords = new List<PluginRecordSource>();
        foreach (var plugin in normalizedManifest.Plugins)
        {
            var pluginPath = CombineRelativePath(fullOutputDirectory, plugin.PluginFileName);
            if (!File.Exists(pluginPath))
            {
                warnings.Add($"生成ESPが見つかりません：{plugin.PluginFileName}");
                requiredFileFailure = true;
                continue;
            }

            try
            {
                var pluginSource = new PluginSource(
                    plugin.PluginFileName,
                    pluginPath,
                    GeneratedModName,
                    fullOutputDirectory,
                    true,
                    true,
                    plugin.PluginFileName.Equals(
                        "GF Music Product.esp",
                        StringComparison.OrdinalIgnoreCase)
                        ? 0
                        : 1,
                    int.MaxValue);
                generatedRecords.AddRange(_pluginRecordScanner.Read(pluginSource));
            }
            catch (Exception exception)
            {
                warnings.Add($"生成ESPを読み込めません：{plugin.PluginFileName} ({exception.Message})");
                requiredFileFailure = true;
    }
}

        ValidateGeneratedRecords(normalizedManifest, generatedRecords, warnings);

        if (!string.IsNullOrWhiteSpace(normalizedManifest.CellSkyPatcherFileName) &&
            !File.Exists(CombineRelativePath(
                fullOutputDirectory,
                normalizedManifest.CellSkyPatcherFileName)))
        {
            warnings.Add($"Cell用SkyPatcher設定が見つかりません：{normalizedManifest.CellSkyPatcherFileName}");
            requiredFileFailure = true;
        }

        ValidateAssets(normalizedManifest, fullOutputDirectory, warnings, ref requiredFileFailure);

        if (normalizedManifest.PlanEntries.Count == 0)
        {
            warnings.Add("GFMusicProduct.jsonが旧形式のため、採用・除外状態を完全には復元できません。採用Trackのみ復元しました。");
        }

        GfMusicManagerLog.Info(
            $"ExistingMusicProductLoader: detected={true}, complete={!requiredFileFailure && warnings.Count == 0}, " +
            $"schema={normalizedManifest.SchemaVersion}, plugins={normalizedManifest.Plugins.Count}, " +
            $"tracks={normalizedManifest.Tracks.Count}, planEntries={normalizedManifest.PlanEntries.Count}, " +
            $"warnings={warnings.Count}, path={fullOutputDirectory}.");

        return new ExistingMusicProductLoadResult(
            fullOutputDirectory,
            true,
            !requiredFileFailure && warnings.Count == 0,
            normalizedManifest,
            warnings);
    }

    private static void ValidateGeneratedRecords(
        MusicGenerationManifest manifest,
        IReadOnlyList<PluginRecordSource> records,
        ICollection<string> warnings)
    {
        var recordsByFormKey = records
            .GroupBy(record => record.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var track in manifest.Tracks)
        {
            if (!recordsByFormKey.TryGetValue(track.FormKey, out var record))
            {
                warnings.Add($"生成Music TrackがESP内に見つかりません：{track.FormKey}");
                continue;
            }

            var hasExpectedPath = record.Assets.Any(asset =>
                NormalizeVirtualPath(asset.VirtualPath).Equals(
                    NormalizeVirtualPath(track.VirtualPath),
                    StringComparison.OrdinalIgnoreCase));
            if (!hasExpectedPath)
            {
                warnings.Add($"生成Music Trackの音源パスがJSONと一致しません：{track.FormKey}");
            }
        }

        foreach (var musicType in manifest.IntegratedMusicTypes)
        {
            if (!recordsByFormKey.ContainsKey(musicType.MusicTypeFormKey))
            {
                warnings.Add($"統合用Music TypeがESP内に見つかりません：{musicType.MusicTypeFormKey}");
            }
        }
    }

    private static void ValidateMtd(
        MusicGenerationManifest manifest,
        string mtdPath,
        ICollection<string> warnings)
    {
        try
        {
            var text = File.ReadAllText(mtdPath);
            if (string.IsNullOrWhiteSpace(text))
            {
                warnings.Add($"生成MTDが空です：{manifest.MtdFileName}");
                return;
            }

            if (!text.Contains("[General]", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("[Location]", StringComparison.OrdinalIgnoreCase) &&
                !text.Contains("[Region]", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"生成MTDにMusic Type・Location・Regionの設定セクションがありません：{manifest.MtdFileName}");
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"生成MTDを読み込めません：{manifest.MtdFileName} ({exception.Message})");
        }
    }

    private void ValidateAssets(
        MusicGenerationManifest manifest,
        string outputDirectory,
        ICollection<string> warnings,
        ref bool requiredFileFailure)
    {
        var archiveEntries = new Dictionary<string, IReadOnlySet<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var asset in manifest.Assets)
        {
            if (asset.IsCopied)
            {
                if (string.IsNullOrWhiteSpace(asset.OutputPath))
                {
                    warnings.Add($"コピー音源の出力パスが記録されていません：{asset.VirtualPath}");
                    requiredFileFailure = true;
                    continue;
                }

                var outputPath = CombineRelativePath(outputDirectory, asset.OutputPath);
                if (!File.Exists(outputPath))
                {
                    warnings.Add($"コピー音源が見つかりません：{asset.OutputPath}");
                    requiredFileFailure = true;
                }

                continue;
            }

            if (asset.SourceKind == AssetSourceKind.Loose)
            {
                if (!File.Exists(asset.SourcePath))
                {
                    warnings.Add($"参照音源が見つかりません：{asset.SourcePath}");
                    requiredFileFailure = true;
                }

                continue;
            }

            if (!File.Exists(asset.SourcePath) ||
                string.IsNullOrWhiteSpace(asset.ArchiveEntryPath))
            {
                warnings.Add($"参照BSA音源が見つかりません：{asset.SourcePath} / {asset.ArchiveEntryPath}");
                requiredFileFailure = true;
                continue;
            }

            try
            {
                if (!archiveEntries.TryGetValue(asset.SourcePath, out var entries))
                {
                    entries = _archiveReader
                        .ReadIndex(asset.SourcePath)
                        .Entries
                        .Select(entry => NormalizeVirtualPath(entry.VirtualPath))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    archiveEntries[asset.SourcePath] = entries;
                }

                if (!entries.Contains(NormalizeVirtualPath(asset.ArchiveEntryPath!)))
                {
                    warnings.Add($"参照BSA内の音源エントリが見つかりません：{asset.SourcePath} / {asset.ArchiveEntryPath}");
                    requiredFileFailure = true;
                }
            }
            catch (Exception exception)
            {
                warnings.Add($"参照BSAを確認できません：{asset.SourcePath} ({exception.Message})");
                requiredFileFailure = true;
            }
        }
    }

    private static string CombineRelativePath(string root, string relativePath)
    {
        var normalized = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) ||
            normalized.Split(Path.DirectorySeparatorChar).Any(part => part is "." or ".."))
        {
            return Path.Combine(root, "__invalid_existing_product_path__");
        }

        return Path.Combine(root, normalized);
    }

    private static string NormalizeVirtualPath(string path) => path
        .Replace('/', '\\')
        .TrimStart('\\');
}

/// <summary>
/// Applies a saved manifest to the current scan plan.  Destinations are
/// resolved against the current candidate settings so missing source records
/// become visible warnings instead of fabricated definitions.
/// </summary>
public sealed class MusicGenerationPlanRestorer
{
    private const int RestoreProgressInterval = 32;

    public MusicGenerationPlanRestoreResult Restore(
        MusicGenerationPlan plan,
        MusicGenerationManifest manifest,
        IReadOnlyList<MusicSettingSource> settings,
        IProgress<ScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(settings);

        var warnings = new List<string>();
        var restoredAssetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entriesByAssetKey = plan.Entries.ToDictionary(
            entry => entry.AssetKey,
            StringComparer.OrdinalIgnoreCase);
        var settingsByKey = settings
            .GroupBy(CreateDestinationKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

        plan.KeepVanillaMusic = manifest.KeepVanillaMusic;
        var savedEntries = manifest.PlanEntries ?? Array.Empty<MusicGenerationPlanEntryOutput>();
        var usedLegacyFallback = savedEntries.Count == 0;
        if (usedLegacyFallback)
        {
            savedEntries = manifest.Tracks
                .GroupBy(track => track.AssetKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => new MusicGenerationPlanEntryOutput(
                    group.Key,
                    group.First().VirtualPath,
                    true,
                    group.SelectMany(track => track.DestinationKeys)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                        group.SelectMany(track => track.Conditions).ToArray())
                {
                    Tracks = group
                        .Where(track => !string.IsNullOrWhiteSpace(track.TrackKey))
                        .Select(track => new MusicGenerationTrackPlanOutput(
                            track.TrackKey,
                            track.Conditions))
                        .ToArray()
                })
                .ToArray();

            var savedAssetKeys = savedEntries
                .Select(entry => entry.AssetKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var currentEntry in plan.Entries.Where(entry =>
                         !savedAssetKeys.Contains(entry.AssetKey)))
            {
                currentEntry.IsAdopted = false;
            }
        }

        if (savedEntries.Count > 0)
        {
            progress?.Report(new ScanProgress(
                ScanIssueSeverity.Info,
                "ResultRestore",
                UiText.Get("Progress.ResultRestore"),
                0,
                savedEntries.Count));
        }

        for (var index = 0; index < savedEntries.Count; index++)
        {
            var savedEntry = savedEntries[index];
            if (!entriesByAssetKey.TryGetValue(savedEntry.AssetKey, out var entry))
            {
                warnings.Add(UiText.Format("Main.MissingSavedAudio", savedEntry.VirtualPath));
            }
            else
            {
                restoredAssetKeys.Add(savedEntry.AssetKey);
                entry.IsAdopted = savedEntry.IsAdopted;
                var destinationKeys = savedEntry.DestinationKeys ?? Array.Empty<string>();
                if (TryResolveDestinations(destinationKeys, settingsByKey, out var destinations, out var unresolved))
                {
                    entry.ReplaceDestinations(destinations);
                }
                else if (destinationKeys.Count > 0)
                {
                    warnings.Add(
                        $"保存済み音源のMusic Type割り当てを復元できません：{savedEntry.VirtualPath} " +
                        $"({unresolved}件)");
                }

                if (savedEntry.Tracks is { Count: > 0 })
                {
                    entry.ApplyTrackConditions(savedEntry.Tracks.Select(track =>
                        new MusicGenerationTrackPlan(track.TrackKey, track.Conditions)));
                }
                else if (entry.TryReplaceLegacyConditions(
                             savedEntry.Conditions ?? Array.Empty<MusicConditionSource>()))
                {
                }
                else if (savedEntry.Conditions is { Count: > 0 })
                {
                    warnings.Add(
                        $"旧形式の音源単位の再生条件は、複数Trackの音源へ一括適用せず解析結果を保持しました：" +
                        $"{savedEntry.VirtualPath} ({entry.Tracks.Count}件)");
                }
            }

            if ((index + 1) % RestoreProgressInterval == 0 || index + 1 == savedEntries.Count)
            {
                progress?.Report(new ScanProgress(
                    ScanIssueSeverity.Info,
                    "ResultRestore",
                    UiText.Get("Progress.ResultRestore"),
                    index + 1,
                    savedEntries.Count,
                    SourcePath: savedEntry.VirtualPath));
            }
        }

        var missingEntries = savedEntries
            .Where(saved => !entriesByAssetKey.ContainsKey(saved.AssetKey))
            .ToArray();
        var missingEntriesByMod = missingEntries
            .GroupBy(entry => GetAssetModName(entry.AssetKey), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.OrdinalIgnoreCase);
        var missingEntryCount = missingEntries.Length;
        var unresolvedDestinationCount = warnings.Count(warning =>
            warning.Contains("Music Type割り当て", StringComparison.Ordinal));
        GfMusicManagerLog.Info(
            $"MusicGenerationPlanRestorer: restored={restoredAssetKeys.Count}, " +
            $"missing={missingEntryCount}, unresolvedDestinations={unresolvedDestinationCount}, " +
            $"legacyFallback={usedLegacyFallback}, keepVanilla={manifest.KeepVanillaMusic}, " +
            $"missingMods={string.Join(',', missingEntriesByMod.Select(item => $"{item.Key}:{item.Value}"))}.");

        var hasSavedState = (manifest.PlanEntries?.Count ?? 0) > 0 ||
                            (manifest.Tracks?.Count ?? 0) > 0;
        return new MusicGenerationPlanRestoreResult(
            restoredAssetKeys.Count > 0 || savedEntries.Count == 0,
            hasSavedState &&
            missingEntryCount == 0 &&
            unresolvedDestinationCount == 0,
            usedLegacyFallback,
            restoredAssetKeys.Count,
            missingEntryCount,
            unresolvedDestinationCount,
            restoredAssetKeys,
            warnings)
        {
            MissingEntriesByMod = missingEntriesByMod
        };
    }

    private static string GetAssetModName(string assetKey)
    {
        var parts = assetKey.Split('\u001f');
        return parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
            ? parts[1]
            : "不明なMOD";
    }

    private static bool TryResolveDestinations(
        IReadOnlyList<string> destinationKeys,
        IReadOnlyDictionary<string, MusicSettingSource> settingsByKey,
        out IReadOnlyList<MusicSettingSource> destinations,
        out int unresolvedCount)
    {
        var resolved = new List<MusicSettingSource>(destinationKeys.Count);
        unresolvedCount = 0;
        foreach (var destinationKey in destinationKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsGeneratedDestinationKey(destinationKey))
            {
                continue;
            }

            if (!TryParseDestinationKey(destinationKey, out _) ||
                !settingsByKey.TryGetValue(destinationKey, out var setting))
            {
                unresolvedCount++;
                continue;
            }

            resolved.Add(setting);
        }

        destinations = resolved;
        return unresolvedCount == 0;
    }

    private static bool IsGeneratedDestinationKey(string value) =>
        value.Contains(":GF Music Product", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseDestinationKey(
        string value,
        out MusicSettingKey key)
    {
        var parts = value.Split('\u001f');
        if (parts.Length != 3 ||
            !Enum.TryParse<MusicSettingScope>(parts[0], true, out var scope) ||
            string.IsNullOrWhiteSpace(parts[1]) ||
            string.IsNullOrWhiteSpace(parts[2]))
        {
            key = default!;
            return false;
        }

        key = new MusicSettingKey(scope, parts[1], parts[2]);
        return true;
    }

    private static string CreateDestinationKey(MusicSettingSource setting) => string.Join(
        "\u001f",
        setting.Scope,
        setting.ScopeFormKey,
        setting.MusicTypeFormKey);
}
