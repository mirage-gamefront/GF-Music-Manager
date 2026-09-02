using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;
using Mutagen.Bethesda.Plugins;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// Options for DFG output.  Music Track forms are imported as JSON, while
/// existing Music Type records are changed through DFG external patches.
/// </summary>
public sealed record DfgMusicGenerationOutputOptions
{
    public required string OutputModDirectory { get; init; }

    public string PackageName { get; init; } = "GF Music Manager DFG";

    public bool OverwriteExisting { get; init; }

    /// <summary>
    /// True when MTD, Cell SkyPatcher, and optional WorldSpace ESP output are
    /// emitted by the shared normal pipeline in the same generated MOD.
    /// </summary>
    public bool CommonAssignmentsProvided { get; init; }

    /// <summary>
    /// Writes the DFG package into a directory already staged by the shared
    /// normal output writer.  The caller owns the atomic commit in this mode.
    /// </summary>
    public bool WriteIntoExistingOutputDirectory { get; init; }

    /// <summary>
    /// Generated bridge Music Types that remain in the shared ESP so Cell and
    /// WorldSpace records can reference a stable FormID.  Their Track lists
    /// are populated by DFG external patches using DFG EditorIDs.
    /// </summary>
    public IReadOnlyList<DfgMusicTypePatchTarget> BridgeMusicTypeTargets { get; init; } =
        Array.Empty<DfgMusicTypePatchTarget>();
}

/// <summary>
/// DFG-specific reference input for a generated bridge Music Type.  This is
/// intentionally separate from the normal ESP Music Type/Track output model.
/// </summary>
public sealed record DfgMusicTypePatchTarget(
    string TargetKey,
    MusicSettingScope Scope,
    string ScopeFormKey,
    string? ScopeEditorId,
    string MusicTypeFormKey,
    string MusicTypeEditorId,
    IReadOnlyList<MusicTrackSource> OfficialTracks,
    IReadOnlyList<MusicGenerationPlanEntry> GeneratedEntries);

public sealed record DfgMusicGenerationOutputResult(
    string OutputModDirectory,
    string PackageDirectory,
    string ManifestPath,
    string MetadataPath,
    string PackageDatabasePath,
    IReadOnlyList<string> ImportPaths,
    int MusicTrackCount,
    int MusicTypeCount,
    int ExternalMusicTypePatchCount,
    int OfficialReferenceCount,
    int UnsupportedAssignmentCount);

public sealed class DfgMusicGenerationOutputException : InvalidOperationException
{
    public DfgMusicGenerationOutputException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Converts the existing generation plan into a DFG package.  New Music Track
/// forms are imported from JSON; each existing Music Type is patched through
/// the package database so its Track list becomes the resolved GFMM list.
/// Cell/Location/Region assignments remain in the shared output pipeline; the
/// optional WorldSpace override is also emitted there when enabled.  This
/// writer only owns the DFG Track imports and Music Type external patches.
/// </summary>
public sealed class DfgMusicGenerationOutputWriter
{
    private static readonly string DataRoot = Path.Combine(
        "Viny Mods",
        "Dynamic Forms Generator",
        "Packages");
    public const string MetadataFileName = DfgMusicGenerationPackageValidator.MetadataFileName;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly MusicGenerationPlanResolver _planResolver;
    private readonly DfgExternalPatchDatabaseWriter _externalPatchDatabaseWriter;

    public DfgMusicGenerationOutputWriter(
        MusicGenerationPlanResolver? planResolver = null,
        DfgExternalPatchDatabaseWriter? externalPatchDatabaseWriter = null)
    {
        _planResolver = planResolver ?? new MusicGenerationPlanResolver();
        _externalPatchDatabaseWriter = externalPatchDatabaseWriter ??
            new DfgExternalPatchDatabaseWriter();
    }

    public DfgMusicGenerationOutputResult Generate(
        MusicGenerationPlan plan,
        IReadOnlyList<MusicSettingSource> settings,
        DfgMusicGenerationOutputOptions options,
        CancellationToken cancellationToken = default)
        => GenerateCore(plan, settings, options, null, cancellationToken);

    public DfgMusicGenerationOutputResult Generate(
        MusicGenerationPlan plan,
        IReadOnlyList<MusicSettingSource> settings,
        DfgMusicGenerationOutputOptions options,
        MusicGenerationPlanResolution planResolution,
        CancellationToken cancellationToken = default)
        => GenerateCore(plan, settings, options, planResolution, cancellationToken);

    private DfgMusicGenerationOutputResult GenerateCore(
        MusicGenerationPlan plan,
        IReadOnlyList<MusicSettingSource> settings,
        DfgMusicGenerationOutputOptions options,
        MusicGenerationPlanResolution? suppliedResolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.OutputModDirectory))
        {
            throw new DfgMusicGenerationOutputException(
                "DFG出力先のMODフォルダが指定されていません。\n" +
                "--output-mod で空の出力先を指定してください。");
        }

        if (string.IsNullOrWhiteSpace(options.PackageName))
        {
            throw new DfgMusicGenerationOutputException(
                "DFGパッケージ名が空です。");
        }

        if (plan.KeepVanillaMusic is null)
        {
            throw new DfgMusicGenerationOutputException(
                "バニラ音源を残すか、残さないかが決まっていません。");
        }

        var adoptedEntries = plan.Entries
            .Where(entry => entry.IsAdopted)
            .ToArray();
        if (adoptedEntries.Length == 0)
        {
            throw new DfgMusicGenerationOutputException(
                "採用された音源がありません。少なくとも1曲を採用してください。");
        }

        if (adoptedEntries.Any(entry => entry.Asset is null))
        {
            throw new DfgMusicGenerationOutputException(
                "採用済み音源の実体を取得できない項目があります。");
        }

        var resolution = suppliedResolution ?? _planResolver.Resolve(plan, settings);
        var outputDirectory = Path.GetFullPath(options.OutputModDirectory);
        if (options.WriteIntoExistingOutputDirectory)
        {
            if (!Directory.Exists(outputDirectory))
            {
                throw new DfgMusicGenerationOutputException(
                    $"共通生成処理のステージ出力先が存在しません：{outputDirectory}");
            }
        }
        else
        {
            ValidateOutputPath(outputDirectory, options.OverwriteExisting);
        }

        var typeSources = BuildTypeSources(settings, resolution);
        var referencedEntries = adoptedEntries
            .DistinctBy(entry => entry.AssetKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var generatedTracks = BuildGeneratedTracks(
            referencedEntries,
            options.PackageName,
            cancellationToken);
        var generatedTrackByKey = generatedTracks.ToDictionary(
            track => track.Identity,
            StringComparer.OrdinalIgnoreCase);

        var pluginDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var musicTypePatches = new List<GeneratedMusicTypePatchDescriptor>();
        var externalPatches = new List<DfgExternalMusicTypePatch>();
        var usedPatchEditorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var officialReferenceCount = 0;

        foreach (var type in resolution.MusicTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            typeSources.TryGetValue(type.MusicTypeFormKey, out var typeSource);
            var trackReferences = BuildTrackReferences(
                type.OfficialTracks,
                type.GeneratedEntries,
                generatedTrackByKey,
                pluginDependencies,
                ref officialReferenceCount);
            var patchEditorId = MakeUniqueEditorId(
                "GFMM_DFG_PATCH_MUSC_" + ShortHash(type.MusicTypeFormKey),
                usedPatchEditorIds);
            var target = ParseExternalMusicTypeTarget(type.MusicTypeFormKey);
            var winningPlugin = ResolveWinningMusicTypePlugin(
                settings,
                type.MusicTypeFormKey,
                target.SourcePlugin);
            pluginDependencies.Add(target.SourcePlugin);
            if (!string.IsNullOrWhiteSpace(winningPlugin))
            {
                pluginDependencies.Add(winningPlugin);
            }

            externalPatches.Add(new DfgExternalMusicTypePatch(
                target.SourcePlugin,
                target.LocalFormId,
                "MusicType",
                patchEditorId,
                winningPlugin,
                BuildMusicTypeTrackPatchJson(trackReferences)));
            musicTypePatches.Add(new GeneratedMusicTypePatchDescriptor(
                patchEditorId,
                type.MusicTypeFormKey,
                typeSource?.EditorId,
                typeSource?.Scopes ?? Array.Empty<DfgScopeDescriptor>(),
                trackReferences,
                type.MusicTypeFormKey,
                typeSource?.EditorId,
                false));
        }

        foreach (var bridge in options.BridgeMusicTypeTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trackReferences = BuildTrackReferences(
                bridge.OfficialTracks,
                bridge.GeneratedEntries,
                generatedTrackByKey,
                pluginDependencies,
                ref officialReferenceCount);
            var patchEditorId = MakeUniqueEditorId(
                "GFMM_DFG_PATCH_BRIDGE_MUSC_" + ShortHash(bridge.TargetKey),
                usedPatchEditorIds);
            var target = ParseExternalMusicTypeTarget(bridge.MusicTypeFormKey);
            pluginDependencies.Add(target.SourcePlugin);
            externalPatches.Add(new DfgExternalMusicTypePatch(
                target.SourcePlugin,
                target.LocalFormId,
                "MusicType",
                patchEditorId,
                target.SourcePlugin,
                BuildMusicTypeTrackPatchJson(trackReferences)));
            musicTypePatches.Add(new GeneratedMusicTypePatchDescriptor(
                patchEditorId,
                null,
                null,
                new[]
                {
                    new DfgScopeDescriptor(
                        bridge.Scope.ToString(),
                        bridge.ScopeFormKey,
                        bridge.ScopeEditorId)
                },
                trackReferences,
                bridge.MusicTypeFormKey,
                bridge.MusicTypeEditorId,
                true));
        }

        var unsupportedAssignments = options.CommonAssignmentsProvided
            ? Array.Empty<DfgUnsupportedAssignment>()
            : BuildUnsupportedAssignments(adoptedEntries);
        var packageFolder = SanitizePackageFolder(options.PackageName);
        var packageId = PackageIdFromName(options.PackageName);
        var pluginDependencyDocuments = pluginDependencies
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(value => new DfgPluginDependencyDocument(value, true))
            .ToArray();
        var manifest = new DfgManifestDocument(
            1,
            packageId,
            options.PackageName,
            "1.0.0",
            true,
            0,
            "package.db",
            Array.Empty<DfgPackageDependencyDocument>(),
            pluginDependencyDocuments);

        var trackDocuments = generatedTracks
            .Select(track => track.Document)
            .ToArray();
        var metadata = new DfgOutputMetadataDocument(
            1,
            "dfg-music-track-import-and-music-type-external-patch",
            options.PackageName,
            packageFolder,
            plan.KeepVanillaMusic.Value,
            generatedTracks
                .Select(track => new DfgOutputTrackMetadata(
                    track.Document.EditorId,
                    track.AssetKey,
                    track.TrackKey,
                    track.Document.MusicTrackPath,
                    track.Document.Conditions.Count))
                .ToArray(),
            musicTypePatches
                .Select(type => new DfgOutputTypeMetadata(
                    type.EditorId,
                    type.SourceMusicTypeFormKey,
                    type.SourceMusicTypeEditorId,
                    type.Scopes,
                    "external-patch",
                    type.TargetMusicTypeFormKey,
                    type.TargetMusicTypeEditorId,
                    type.IsBridgeType))
                .ToArray(),
            unsupportedAssignments,
            new[]
            {
                "採用音源ごとにMusic Trackを作成し、既存Music TypeのTrack一覧をDFG外部パッチで置換します。",
                "バニラ音源を残す場合は公式Music Trackを含め、残さない場合は生成Music Trackだけを登録します。",
                options.CommonAssignmentsProvided
                    ? "Cell・Location・Region・WorldSpaceへの割り当ては共通の通常出力で処理します。"
                    : "既存のCell・Location・Region・WorldSpaceへの割り当ては出力しません。",
                options.CommonAssignmentsProvided
                    ? "音源ファイルは共通の通常出力で配置します。"
                    : "ESP・ESL・MTD・音源ファイルのコピーは出力しません。",
                "DFGがこのパッケージを読み込み、外部パッチをゲーム内へ適用する必要があります。"
            });

        var writeIntoExistingOutput = options.WriteIntoExistingOutputDirectory;
        var stageDirectory = writeIntoExistingOutput
            ? outputDirectory
            : outputDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                $".generating-{Guid.NewGuid():N}";
        var importPaths = new List<string>(trackDocuments.Length);
        var committed = writeIntoExistingOutput;
        string? backupDirectory = null;

        try
        {
            var packageDirectory = Path.Combine(stageDirectory, DataRoot, packageFolder);
            var importsDirectory = Path.Combine(packageDirectory, "imports");
            Directory.CreateDirectory(importsDirectory);

            WriteJson(Path.Combine(packageDirectory, "manifest.json"), manifest);
            foreach (var document in trackDocuments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(importsDirectory, document.EditorId + ".json");
                WriteJson(path, document);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var packageDatabasePath = Path.Combine(packageDirectory, "package.db");
            _externalPatchDatabaseWriter.Write(packageDatabasePath, externalPatches);

            var metadataPath = Path.Combine(stageDirectory, MetadataFileName);
            WriteJson(metadataPath, metadata);

            if (!writeIntoExistingOutput && Directory.Exists(outputDirectory))
            {
                backupDirectory = outputDirectory.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar) +
                    $".backup-{Guid.NewGuid():N}";
                Directory.Move(outputDirectory, backupDirectory);
            }

            if (!writeIntoExistingOutput)
            {
                Directory.Move(stageDirectory, outputDirectory);
                committed = true;
            }

            if (backupDirectory is not null)
            {
                Directory.Delete(backupDirectory, true);
                backupDirectory = null;
            }

            var finalPackageDirectory = Path.Combine(outputDirectory, DataRoot, packageFolder);
            var finalManifestPath = Path.Combine(finalPackageDirectory, "manifest.json");
            var finalPackageDatabasePath = Path.Combine(finalPackageDirectory, "package.db");
            var finalMetadataPath = Path.Combine(outputDirectory, MetadataFileName);
            importPaths.AddRange(
                trackDocuments
                    .Select(document => Path.Combine(
                        finalPackageDirectory,
                        "imports",
                        document.EditorId + ".json")));

            return new DfgMusicGenerationOutputResult(
                outputDirectory,
                finalPackageDirectory,
                finalManifestPath,
                finalMetadataPath,
                finalPackageDatabasePath,
                importPaths,
                trackDocuments.Length,
                musicTypePatches.Count,
                externalPatches.Count,
                officialReferenceCount,
                unsupportedAssignments.Count);
        }
        catch (DfgMusicGenerationOutputException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DfgMusicGenerationOutputException(
                $"DFGパッケージの出力に失敗しました：{exception.Message}");
        }
        finally
        {
            if (!committed && Directory.Exists(stageDirectory))
            {
                Directory.Delete(stageDirectory, true);
            }

            if (!committed && backupDirectory is not null &&
                !Directory.Exists(outputDirectory) &&
                Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, outputDirectory);
            }
        }
    }

    private static void ValidateOutputPath(
        string outputDirectory,
        bool overwriteExisting)
    {
        if (File.Exists(outputDirectory))
        {
            throw new DfgMusicGenerationOutputException(
                $"DFG出力先に同名ファイルがあります：{outputDirectory}");
        }

        if (Directory.Exists(outputDirectory) && !overwriteExisting)
        {
            throw new DfgMusicGenerationOutputException(
                $"DFG出力先がすでに存在します。再生成する場合は --overwrite を指定してください：{outputDirectory}");
        }
    }

    private static IReadOnlyDictionary<string, DfgTypeSource> BuildTypeSources(
        IReadOnlyList<MusicSettingSource> settings,
        MusicGenerationPlanResolution resolution)
    {
        var keys = resolution.MusicTypes
            .Select(type => type.MusicTypeFormKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return settings
            .Where(setting => keys.Contains(setting.MusicTypeFormKey))
            .GroupBy(setting => setting.MusicTypeFormKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new DfgTypeSource(
                    group.Select(setting => setting.MusicTypeEditorId)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                    group.Select(setting => new DfgScopeDescriptor(
                            setting.Scope.ToString(),
                            setting.ScopeFormKey,
                            setting.ScopeEditorId))
                        .DistinctBy(scope => string.Join("\u001f", scope.Scope, scope.FormKey), StringComparer.OrdinalIgnoreCase)
                        .ToArray()),
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<GeneratedTrackDescriptor> BuildGeneratedTracks(
        IReadOnlyList<MusicGenerationPlanEntry> entries,
        string packageName,
        CancellationToken cancellationToken)
    {
        var result = new List<GeneratedTrackDescriptor>();
        var usedEditorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OrderBy(value => value.AssetKey, StringComparer.OrdinalIgnoreCase))
        {
            if (entry.Asset is null)
            {
                throw new DfgMusicGenerationOutputException(
                    $"採用音源の実体を取得できません：{entry.AssetKey}");
            }

            foreach (var track in entry.Tracks.OrderBy(value => value.TrackKey, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identity = CreateTrackIdentity(entry.AssetKey, track.TrackKey);
                var editorId = MakeUniqueEditorId(
                    "GFMM_DFG_MUST_" + ShortHash(identity),
                    usedEditorIds);
                result.Add(new GeneratedTrackDescriptor(
                    identity,
                    entry.AssetKey,
                    track.TrackKey,
                    new DfgMusicTrackDocument(
                        1,
                        editorId,
                        "MusicTrack",
                        "MUST",
                        packageName,
                        NormalizeVirtualPath(entry.Asset.VirtualPath),
                        string.Empty,
                        Array.Empty<float>(),
                        0,
                        0,
                        0,
                        ConvertConditions(track.Conditions)),
                    track.Conditions));
            }
        }

        return result;
    }

    private static IReadOnlyList<DfgFormReferenceDocument> BuildTrackReferences(
        IEnumerable<MusicTrackSource> officialTracks,
        IEnumerable<MusicGenerationPlanEntry> generatedEntries,
        IReadOnlyDictionary<string, GeneratedTrackDescriptor> generatedTracks,
        ISet<string> pluginDependencies,
        ref int officialReferenceCount)
    {
        var result = new List<DfgFormReferenceDocument>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in officialTracks)
        {
            var formId = FormatDfgFormId(track.FormKey, "公式Music Track");
            var reference = new DfgFormReferenceDocument(track.EditorId, formId);
            if (keys.Add(reference.Identity))
            {
                result.Add(reference);
                officialReferenceCount++;
                pluginDependencies.Add(GetPluginName(track.FormKey, "公式Music Track"));
            }
        }

        foreach (var entry in generatedEntries)
        {
            foreach (var track in entry.Tracks)
            {
                var identity = CreateTrackIdentity(entry.AssetKey, track.TrackKey);
                if (!generatedTracks.TryGetValue(identity, out var generated))
                {
                    throw new DfgMusicGenerationOutputException(
                        $"生成Trackの参照を作成できません：{identity}");
                }

                var reference = new DfgFormReferenceDocument(
                    generated.Document.EditorId,
                    null);
                if (keys.Add(reference.Identity))
                {
                    result.Add(reference);
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<DfgUnsupportedAssignment> BuildUnsupportedAssignments(
        IEnumerable<MusicGenerationPlanEntry> adoptedEntries)
    {
        var result = new List<DfgUnsupportedAssignment>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in adoptedEntries)
        {
            foreach (var destination in entry.DestinationKeys)
            {
                if (destination.Scope == MusicSettingScope.MusicType)
                {
                    continue;
                }

                var key = string.Join(
                    "\u001f",
                    destination.Scope,
                    destination.ScopeFormKey,
                    destination.MusicTypeFormKey);
                if (!keys.Add(key))
                {
                    continue;
                }

                result.Add(new DfgUnsupportedAssignment(
                    destination.Scope.ToString(),
                    destination.ScopeFormKey,
                    destination.MusicTypeFormKey,
                    null));
            }
        }

        return result;
    }

    private static (string SourcePlugin, uint LocalFormId) ParseExternalMusicTypeTarget(
        string formKey)
    {
        try
        {
            var parsed = FormKey.Factory(formKey);
            var plugin = parsed.ModKey.FileName.String;
            var localFormId = checked((uint)parsed.ID);
            if (string.IsNullOrWhiteSpace(plugin) || localFormId == 0)
            {
                throw new FormatException();
            }

            return (plugin, localFormId);
        }
        catch
        {
            throw new DfgMusicGenerationOutputException(
                $"Music Typeの外部パッチ先FormIDを解釈できません：{formKey}");
        }
    }

    private static string ResolveWinningMusicTypePlugin(
        IReadOnlyList<MusicSettingSource> settings,
        string musicTypeFormKey,
        string fallbackPlugin)
    {
        var winning = settings
            .Where(setting => setting.MusicTypeFormKey.Equals(
                musicTypeFormKey,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(setting => setting.MusicTypeRecord.IsWinner)
            .ThenByDescending(setting => setting.MusicTypeRecord.Plugin.LoadOrderIndex)
            .ThenByDescending(setting => setting.MusicTypeRecord.Plugin.ModPriority)
            .Select(setting => setting.MusicTypeRecord.Plugin.Name)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return winning ?? fallbackPlugin;
    }

    private static string BuildMusicTypeTrackPatchJson(
        IReadOnlyList<DfgFormReferenceDocument> trackReferences)
    {
        var document = new DfgExternalChangesDocument(
            1,
            new Dictionary<string, DfgExternalFieldChangeDocument>(StringComparer.Ordinal)
            {
                ["musicTypeTracks"] = new DfgExternalFieldChangeDocument(
                    "replace",
                    trackReferences)
            });
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static IReadOnlyList<DfgConditionDocument> ConvertConditions(
        IReadOnlyList<MusicConditionSource> conditions)
    {
        var result = new List<DfgConditionDocument>(conditions.Count);
        foreach (var condition in conditions)
        {
            if (!condition.IsEditable)
            {
                throw new DfgMusicGenerationOutputException(
                    $"DFG出力に未対応の再生条件があります：{condition.FunctionName} ({condition.DataType})");
            }

            if (!condition.ComparisonValueType.Equals(
                    "Float",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new DfgMusicGenerationOutputException(
                    $"DFG出力に未対応の比較値があります：{condition.FunctionName} ({condition.ComparisonValueType})");
            }

            uint functionId = condition.FunctionName.ToLowerInvariant() switch
            {
                "getcurrenttime" => 18,
                "getiscurrentweather" => 149,
                "getcombattargethaskeyword" => 707,
                _ => throw new DfgMusicGenerationOutputException(
                    $"DFG出力に未対応の再生条件があります：{condition.FunctionName}")
            };
            var parameter = functionId switch
            {
                149 => FormatDfgFormId(condition.WeatherFormKey, "天候"),
                707 => FormatDfgFormId(condition.KeywordFormKey, "戦闘キーワード"),
                _ => string.Empty
            };
            var runOn = ResolveRunOnType(condition);
            var comparisonGlobal = string.IsNullOrWhiteSpace(condition.ComparisonGlobalFormKey)
                ? string.Empty
                : FormatDfgFormId(condition.ComparisonGlobalFormKey, "比較Global");
            var runOnRef = string.IsNullOrWhiteSpace(condition.ReferenceFormKey)
                ? string.Empty
                : FormatDfgFormId(condition.ReferenceFormKey, "再生条件の実行対象");
            result.Add(new DfgConditionDocument(
                "Raw",
                condition.FunctionName,
                functionId,
                ResolveCompareOperator(condition.CompareOperator),
                condition.ComparisonValue,
                condition.Flags.Equals("OR", StringComparison.OrdinalIgnoreCase),
                condition.UseAliases,
                !string.IsNullOrWhiteSpace(comparisonGlobal),
                condition.UsePackageData,
                false,
                runOn,
                uint.MaxValue,
                runOnRef,
                comparisonGlobal,
                parameter,
                string.Empty));
        }

        return result;
    }

    private static uint ResolveCompareOperator(string value) => value.ToLowerInvariant() switch
    {
        "equalto" => 0,
        "notequalto" => 1,
        "greaterthan" => 2,
        "greaterthanorequalto" => 3,
        "lessthan" => 4,
        "lessthanorequalto" => 5,
        _ => throw new DfgMusicGenerationOutputException(
            $"DFG出力に未対応の比較演算子があります：{value}")
    };

    private static uint ResolveRunOnType(MusicConditionSource condition)
    {
        if (condition.RunOnTypeIndex is >= 0 and <= 8)
        {
            return (uint)condition.RunOnTypeIndex;
        }

        return condition.RunOnType.ToLowerInvariant() switch
        {
            "subject" => 0,
            "target" => 1,
            "reference" => 2,
            "combattarget" => 3,
            "linkedref" => 4,
            "questalias" => 5,
            "packagedata" => 6,
            "eventdata" => 7,
            "commandtarget" => 8,
            _ => throw new DfgMusicGenerationOutputException(
                $"DFG出力に未対応の実行対象があります：{condition.RunOnType}")
        };
    }

    private static string FormatDfgFormId(string? formKey, string label)
    {
        if (string.IsNullOrWhiteSpace(formKey))
        {
            throw new DfgMusicGenerationOutputException(
                $"{label}のFormIDがありません。");
        }

        try
        {
            var parsed = FormKey.Factory(formKey);
            return $"{parsed.ModKey.FileName.String}|{parsed.ID:X}";
        }
        catch
        {
            throw new DfgMusicGenerationOutputException(
                $"{label}のFormIDを解釈できません：{formKey}");
        }
    }

    private static string GetPluginName(string formKey, string label)
    {
        try
        {
            return FormKey.Factory(formKey).ModKey.FileName.String;
        }
        catch
        {
            throw new DfgMusicGenerationOutputException(
                $"{label}のFormIDを解釈できません：{formKey}");
        }
    }

    private static string NormalizeVirtualPath(string value) => value
        .Replace('\\', '/')
        .TrimStart('/');

    private static string CreateTrackIdentity(string assetKey, string trackKey) =>
        string.Join("\u001f", assetKey, trackKey);

    private static string MakeUniqueEditorId(
        string candidate,
        ISet<string> used)
    {
        var value = new string(candidate
            .Where(character => char.IsLetterOrDigit(character) || character == '_')
            .ToArray());
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "GFMM_DFG_Form";
        }

        var result = value;
        var suffix = 2;
        while (!used.Add(result))
        {
            result = $"{value}_{suffix++}";
        }

        return result;
    }

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];

    private static string SanitizePackageFolder(string name)
    {
        var result = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            var valid = character <= 0x7F &&
                        (char.IsLetterOrDigit(character) ||
                         character is '_' or '-' or '.');
            result.Append(valid ? character : '_');
        }

        return result.Length == 0 ? "Local_Forms" : result.ToString();
    }

    private static string PackageIdFromName(string name)
    {
        var result = new StringBuilder(name.Length);
        var pendingSeparator = false;
        foreach (var character in name)
        {
            var asciiAlphaNumeric = character <= 0x7F &&
                                    char.IsLetterOrDigit(character);
            if (asciiAlphaNumeric)
            {
                if (pendingSeparator && result.Length > 0)
                {
                    result.Append('-');
                }

                result.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return result.Length == 0 ? "dfg-package" : result.ToString();
    }

    private static void WriteJson(string path, object value)
    {
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(value, JsonOptions),
            new UTF8Encoding(false));
    }

    private sealed record DfgTypeSource(
        string? EditorId,
        IReadOnlyList<DfgScopeDescriptor> Scopes);

    private sealed record GeneratedTrackDescriptor(
        string Identity,
        string AssetKey,
        string TrackKey,
        DfgMusicTrackDocument Document,
        IReadOnlyList<MusicConditionSource> Conditions);

    private sealed record GeneratedMusicTypePatchDescriptor(
        string EditorId,
        string? SourceMusicTypeFormKey,
        string? SourceMusicTypeEditorId,
        IReadOnlyList<DfgScopeDescriptor> Scopes,
        IReadOnlyList<DfgFormReferenceDocument> TrackReferences,
        string TargetMusicTypeFormKey,
        string? TargetMusicTypeEditorId,
        bool IsBridgeType);

    private sealed record DfgExternalChangesDocument(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("fields")] IReadOnlyDictionary<string, DfgExternalFieldChangeDocument> Fields);

    private sealed record DfgExternalFieldChangeDocument(
        [property: JsonPropertyName("operation")] string Operation,
        [property: JsonPropertyName("value")] IReadOnlyList<DfgFormReferenceDocument> Value);

    private sealed record DfgManifestDocument(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("packageId")] string PackageId,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("enabled")] bool Enabled,
        [property: JsonPropertyName("priority")] int Priority,
        [property: JsonPropertyName("database")] string Database,
        [property: JsonPropertyName("dependencies")] IReadOnlyList<DfgPackageDependencyDocument> Dependencies,
        [property: JsonPropertyName("pluginDependencies")] IReadOnlyList<DfgPluginDependencyDocument> PluginDependencies);

    private sealed record DfgPackageDependencyDocument(
        [property: JsonPropertyName("packageId")] string PackageId,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("required")] bool Required);

    private sealed record DfgPluginDependencyDocument(
        [property: JsonPropertyName("plugin")] string Plugin,
        [property: JsonPropertyName("required")] bool Required);

    private sealed record DfgFormReferenceDocument(
        [property: JsonPropertyName("editorID")] string? EditorId,
        [property: JsonPropertyName("formID")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? FormId)
    {
        [JsonIgnore]
        public string Identity => string.Join("\u001f", EditorId ?? string.Empty, FormId ?? string.Empty);
    }

    private sealed record DfgMusicTrackDocument(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("editorId")] string EditorId,
        [property: JsonPropertyName("formKind")] string FormKind,
        [property: JsonPropertyName("sourceSignature")] string SourceSignature,
        [property: JsonPropertyName("packageName")] string PackageName,
        [property: JsonPropertyName("musicTrackPath")] string MusicTrackPath,
        [property: JsonPropertyName("musicTrackFinalePath")] string MusicTrackFinalePath,
        [property: JsonPropertyName("musicTrackCuePoints")] IReadOnlyList<float> MusicTrackCuePoints,
        [property: JsonPropertyName("musicTrackLoopBegin")] float MusicTrackLoopBegin,
        [property: JsonPropertyName("musicTrackLoopEnd")] float MusicTrackLoopEnd,
        [property: JsonPropertyName("musicTrackLoopCount")] uint MusicTrackLoopCount,
        [property: JsonPropertyName("conditions")] IReadOnlyList<DfgConditionDocument> Conditions);

    private sealed record DfgConditionDocument(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("functionName")] string FunctionName,
        [property: JsonPropertyName("functionId")] uint FunctionId,
        [property: JsonPropertyName("opCode")] uint OpCode,
        [property: JsonPropertyName("comparisonValue")] float ComparisonValue,
        [property: JsonPropertyName("isOr")] bool IsOr,
        [property: JsonPropertyName("useAliases")] bool UseAliases,
        [property: JsonPropertyName("useGlobalComparison")] bool UseGlobalComparison,
        [property: JsonPropertyName("usePackData")] bool UsePackData,
        [property: JsonPropertyName("swapTarget")] bool SwapTarget,
        [property: JsonPropertyName("runOn")] uint RunOn,
        [property: JsonPropertyName("dataId")] uint DataId,
        [property: JsonPropertyName("runOnRef")] string RunOnRef,
        [property: JsonPropertyName("comparisonGlobal")] string ComparisonGlobal,
        [property: JsonPropertyName("param1")] string Param1,
        [property: JsonPropertyName("param2")] string Param2);

    private sealed record DfgScopeDescriptor(
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("formKey")] string FormKey,
        [property: JsonPropertyName("editorId")] string? EditorId);

    private sealed record DfgUnsupportedAssignment(
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("scopeFormKey")] string ScopeFormKey,
        [property: JsonPropertyName("sourceMusicTypeFormKey")] string MusicTypeFormKey,
        [property: JsonPropertyName("generatedMusicTypeEditorId")] string? GeneratedMusicTypeEditorId);

    private sealed record DfgOutputTrackMetadata(
        [property: JsonPropertyName("editorId")] string EditorId,
        [property: JsonPropertyName("assetKey")] string AssetKey,
        [property: JsonPropertyName("trackKey")] string TrackKey,
        [property: JsonPropertyName("audioPath")] string AudioPath,
        [property: JsonPropertyName("conditionCount")] int ConditionCount);

    private sealed record DfgOutputTypeMetadata(
        [property: JsonPropertyName("editorId")] string EditorId,
        [property: JsonPropertyName("sourceMusicTypeFormKey")] string? SourceMusicTypeFormKey,
        [property: JsonPropertyName("sourceMusicTypeEditorId")] string? SourceMusicTypeEditorId,
        [property: JsonPropertyName("scopes")] IReadOnlyList<DfgScopeDescriptor> Scopes,
        [property: JsonPropertyName("output")] string Output,
        [property: JsonPropertyName("targetMusicTypeFormKey")] string? TargetMusicTypeFormKey,
        [property: JsonPropertyName("targetMusicTypeEditorId")] string? TargetMusicTypeEditorId,
        [property: JsonPropertyName("isBridgeType")] bool IsBridgeType);

    private sealed record DfgOutputMetadataDocument(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("packageName")] string PackageName,
        [property: JsonPropertyName("packageFolder")] string PackageFolder,
        [property: JsonPropertyName("keepVanillaMusic")] bool KeepVanillaMusic,
        [property: JsonPropertyName("musicTracks")] IReadOnlyList<DfgOutputTrackMetadata> MusicTracks,
        [property: JsonPropertyName("musicTypes")] IReadOnlyList<DfgOutputTypeMetadata> MusicTypes,
        [property: JsonPropertyName("unsupportedAssignments")] IReadOnlyList<DfgUnsupportedAssignment> UnsupportedAssignments,
        [property: JsonPropertyName("limitations")] IReadOnlyList<string> Limitations);
}
