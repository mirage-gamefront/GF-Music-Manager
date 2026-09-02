using System.Text.Json;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using SkyrimScan.Core.Archives;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Plugins;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// Result of the user-facing validation that runs after a generation is
/// written, but before the staging directory is committed as a MOD.
/// </summary>
public sealed record MusicGenerationDiagnosticResult(
    bool IsSuccess,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> Errors)
{
    public int CheckCount => Checks.Count;
    public int ErrorCount => Errors.Count;

    public string Summary => IsSuccess
        ? $"OK ({CheckCount} checks)"
        : $"Failed ({ErrorCount} error(s), {CheckCount} checks)";

    public string Details => string.Join(Environment.NewLine, Errors);
}

/// <summary>
/// Performs a low-cost, deterministic validation of the generated files.
/// This is deliberately separate from source-MOD scanning: it validates only
/// the output that GF Music Manager is about to commit.
/// </summary>
public sealed class MusicGenerationPostGenerationDiagnostic
{
    private readonly BsaArchiveReader _archiveReader;
    private readonly PluginRecordScanner _pluginRecordScanner = new();
    private readonly DfgMusicGenerationPackageValidator _dfgPackageValidator = new();

    public MusicGenerationPostGenerationDiagnostic(BsaArchiveReader archiveReader)
    {
        _archiveReader = archiveReader ?? throw new ArgumentNullException(nameof(archiveReader));
    }

    public MusicGenerationDiagnosticResult Run(
        string stageDirectory,
        string outputDirectory,
        string mtdFilePath,
        string manifestPath,
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyList<GeneratedPluginOutput> plugins,
        IReadOnlyList<GeneratedMusicTrackOutput> tracks,
        IReadOnlyList<GeneratedWorldSpaceOutput> worldSpaces,
        IReadOnlyList<GeneratedCellOutput> cells,
        IReadOnlyList<GeneratedMusicTypeOutput>? integratedMusicTypes,
        string? cellSkyPatcherFileName,
        IReadOnlyList<GeneratedAssetOutput> assets,
        bool worldSpaceIndividualAssignment,
        int expectedNewRecordCount,
        int maxNewRecordsPerPlugin,
        MusicGenerationPlanResolution? planResolution = null,
        CancellationToken cancellationToken = default,
        MusicGenerationOutputMode outputMode = MusicGenerationOutputMode.Normal,
        DfgMusicGenerationOutputResult? dfgOutput = null,
        string? dfgPackageDirectory = null,
        string? dfgMetadataPath = null,
        int? expectedDfgMusicTrackCount = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(plugins);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(worldSpaces);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(assets);

        var checks = new List<string>();
        var errors = new List<string>();
        var recordsByPlugin = new Dictionary<string, IReadOnlyList<PluginRecordSource>>(
            StringComparer.OrdinalIgnoreCase);

        void Check(string message) => checks.Add(message);
        void Error(string message) => errors.Add(message);

        cancellationToken.ThrowIfCancellationRequested();
        var fullStageDirectory = Path.GetFullPath(stageDirectory);
        var fullOutputDirectory = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(fullStageDirectory))
        {
            Error($"生成ステージフォルダがありません: {fullStageDirectory}");
            return CreateResult(checks, errors);
        }

        ValidateRequiredFiles(
            fullStageDirectory,
            mtdFilePath,
            manifestPath,
            plugins,
            cellSkyPatcherFileName,
            Check,
            Error);

        ValidatePlugins(
            fullStageDirectory,
            plugins,
            recordsByPlugin,
            Check,
            Error,
            cancellationToken);

        if (outputMode == MusicGenerationOutputMode.Dfg)
        {
            ValidateDfgStaticMusicTracks(
                recordsByPlugin,
                Check,
                Error);
        }

        ValidateTracks(
            tracks,
            recordsByPlugin,
            Check,
            Error,
            cancellationToken);

        ValidateWorldSpaces(
            worldSpaces,
            recordsByPlugin,
            Check,
            Error,
            cancellationToken,
            outputMode == MusicGenerationOutputMode.Dfg);

        ValidateIntegratedMusicTypes(
            integratedMusicTypes ?? Array.Empty<GeneratedMusicTypeOutput>(),
            recordsByPlugin,
            Check,
            Error,
            cancellationToken,
            outputMode == MusicGenerationOutputMode.Dfg);

        ValidateMtd(
            mtdFilePath,
            settings,
            tracks,
            worldSpaces,
            integratedMusicTypes ?? Array.Empty<GeneratedMusicTypeOutput>(),
                worldSpaceIndividualAssignment,
                planResolution,
                outputMode,
                Check,
                Error,
            cancellationToken);

        ValidateCellSkyPatcher(
            fullStageDirectory,
            cellSkyPatcherFileName,
            cells,
            Check,
            Error,
            cancellationToken);

        ValidateAssets(
            fullStageDirectory,
            assets,
            Check,
            Error,
            cancellationToken);

        ValidateManifest(
            manifestPath,
            fullOutputDirectory,
            Path.GetFileName(mtdFilePath),
            worldSpaceIndividualAssignment,
            plugins,
            tracks,
            worldSpaces,
            cells,
            integratedMusicTypes ?? Array.Empty<GeneratedMusicTypeOutput>(),
            cellSkyPatcherFileName,
            assets,
            expectedNewRecordCount,
            maxNewRecordsPerPlugin,
            Check,
            Error,
            cancellationToken);

        if (outputMode == MusicGenerationOutputMode.Dfg)
        {
            DfgMusicGenerationPackageValidationResult dfgDiagnostic;
            if (dfgOutput is not null)
            {
                dfgDiagnostic = _dfgPackageValidator.Validate(
                    dfgOutput,
                    cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(dfgPackageDirectory))
            {
                dfgDiagnostic = _dfgPackageValidator.Validate(
                    dfgPackageDirectory,
                    dfgMetadataPath,
                    cancellationToken: cancellationToken);
            }
            else
            {
                dfgDiagnostic = new DfgMusicGenerationPackageValidationResult(
                    false,
                    Array.Empty<string>(),
                    new[] { "DFG packageの出力先がmanifestから解決できません。" });
            }

            foreach (var check in dfgDiagnostic.Checks)
            {
                Check($"DFG: {check}");
            }

            foreach (var errorMessage in dfgDiagnostic.Errors)
            {
                Error($"DFG: {errorMessage}");
            }

            if (expectedDfgMusicTrackCount is not null &&
                dfgOutput is not null)
            {
                if (dfgOutput.MusicTrackCount == expectedDfgMusicTrackCount.Value)
                {
                    Check(
                        $"DFG Music Track件数が採用Track件数と一致（{dfgOutput.MusicTrackCount}件）");
                }
                else
                {
                    Error(
                        $"DFG Music Track件数が採用Track件数と一致しません：" +
                        $"採用 {expectedDfgMusicTrackCount.Value}件 / DFG {dfgOutput.MusicTrackCount}件");
                }
            }

            var packageDatabasePath = dfgOutput?.PackageDatabasePath;
            if (string.IsNullOrWhiteSpace(packageDatabasePath) &&
                !string.IsNullOrWhiteSpace(dfgPackageDirectory))
            {
                packageDatabasePath = Path.Combine(
                    dfgPackageDirectory,
                    "package.db");
            }

            ValidateDfgBridgeMusicTypePatches(
                packageDatabasePath,
                integratedMusicTypes ?? Array.Empty<GeneratedMusicTypeOutput>(),
                worldSpaces,
                Check,
                Error,
                cancellationToken);
        }

        return CreateResult(checks, errors);
    }

    private static MusicGenerationDiagnosticResult CreateResult(
        List<string> checks,
        List<string> errors) =>
        new(errors.Count == 0, checks.ToArray(), errors.ToArray());

    private static void ValidateRequiredFiles(
        string stageDirectory,
        string mtdFilePath,
        string manifestPath,
        IReadOnlyList<GeneratedPluginOutput> plugins,
        string? cellSkyPatcherFileName,
        Action<string> check,
        Action<string> error)
    {
        ValidateFile(Path.Combine(stageDirectory, Path.GetFileName(mtdFilePath)), "MTD", check, error);
        ValidateFile(Path.Combine(stageDirectory, Path.GetFileName(manifestPath)), "manifest", check, error);
        foreach (var plugin in plugins)
        {
            ValidateFile(
                Path.Combine(stageDirectory, plugin.PluginFileName),
                $"ESP {plugin.PluginFileName}",
                check,
                error);
        }

        if (!string.IsNullOrWhiteSpace(cellSkyPatcherFileName))
        {
            ValidateFile(
                Path.Combine(
                    stageDirectory,
                    cellSkyPatcherFileName.Replace('\\', Path.DirectorySeparatorChar)),
                "Cell用SkyPatcher設定",
                check,
                error);
        }
    }

    private static void ValidateFile(
        string path,
        string label,
        Action<string> check,
        Action<string> error)
    {
        if (File.Exists(path))
        {
            check($"{label}: file exists");
        }
        else
        {
            error($"{label}: file is missing ({path})");
        }
    }

    private static void ValidatePlugins(
        string stageDirectory,
        IReadOnlyList<GeneratedPluginOutput> plugins,
        IDictionary<string, IReadOnlyList<PluginRecordSource>> recordsByPlugin,
        Action<string> check,
        Action<string> error,
        CancellationToken cancellationToken)
    {
        var scanner = new PluginRecordScanner();
        foreach (var expected in plugins)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pluginPath = Path.Combine(stageDirectory, expected.PluginFileName);
            if (!File.Exists(pluginPath))
            {
                continue;
            }

            try
            {
                var pluginSource = new PluginSource(
                    expected.PluginFileName,
                    pluginPath,
                    "GF Music Product",
                    stageDirectory,
                    true,
                    true,
                    0,
                    0);
                var issues = new List<ScanIssue>();
                var records = scanner.Read(pluginSource, cancellationToken, issues);
                recordsByPlugin[expected.PluginFileName] = records;

                var musicTrackCount = CountRecords(records, "MusicTrack");
                var musicTypeCount = CountRecords(records, "MusicType");
                var worldSpaceCount = CountRecords(records, "Worldspace");
                ValidateCount(
                    $"{expected.PluginFileName} Music Track",
                    musicTrackCount,
                    expected.NewMusicTrackRecordCount,
                    check,
                    error);
                ValidateCount(
                    $"{expected.PluginFileName} Music Type",
                    musicTypeCount,
                    expected.NewMusicTypeRecordCount,
                    check,
                    error);
                ValidateCount(
                    $"{expected.PluginFileName} WorldSpace override",
                    worldSpaceCount,
                    expected.WorldSpaceOverrideRecordCount,
                    check,
                    error);

                using var generatedPlugin = SkyrimMod.CreateFromBinaryOverlay(
                    new ModPath(
                        ModKey.FromNameAndExtension(expected.PluginFileName),
                        pluginPath),
                    SkyrimRelease.SkyrimSE,
                    new BinaryReadParameters());
                if (generatedPlugin.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Small))
                {
                    check($"{expected.PluginFileName}: ESL flag is present");
                }
                else
                {
                    error($"{expected.PluginFileName}: ESL flag is missing");
                }

                if (issues.Count > 0)
                {
                    check($"{expected.PluginFileName}: record scan completed with {issues.Count} non-fatal issue(s)");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                error($"{expected.PluginFileName}: ESP could not be reopened ({exception.Message})");
            }
        }
    }

    private static void ValidateDfgStaticMusicTracks(
        IReadOnlyDictionary<string, IReadOnlyList<PluginRecordSource>> recordsByPlugin,
        Action<string> check,
        Action<string> error)
    {
        var staticTrackCount = recordsByPlugin.Values.Sum(records =>
            CountRecords(records, "MusicTrack"));
        if (staticTrackCount == 0)
        {
            check("DFG: ESP内Music Trackは0件（DFG packageのみを使用）");
            check("DFG: ESP Music TrackとのTrackKey重複なし");
        }
        else
        {
            error(
                $"DFG: ESP内Music Trackが残っています（{staticTrackCount}件）。" +
                "DFG方式ではMusic TrackをESPへ生成しません。");
        }
    }

    private static void ValidateDfgBridgeMusicTypePatches(
        string? packageDatabasePath,
        IReadOnlyList<GeneratedMusicTypeOutput> integratedMusicTypes,
        IReadOnlyList<GeneratedWorldSpaceOutput> worldSpaces,
        Action<string> check,
        Action<string> error,
        CancellationToken cancellationToken)
    {
        var targets = integratedMusicTypes
            .Select(type => (Label: $"統合用Music Type {type.MusicTypeEditorId}", FormKey: type.MusicTypeFormKey))
            .Concat(worldSpaces.Select(worldSpace =>
                (Label: $"WorldSpace用Music Type {worldSpace.MusicTypeEditorId}",
                    FormKey: worldSpace.MusicTypeFormKey)))
            .ToArray();
        if (targets.Length == 0)
        {
            check("DFG: 統合用Music Typeの外部パッチは不要");
            return;
        }

        if (string.IsNullOrWhiteSpace(packageDatabasePath) ||
            !File.Exists(packageDatabasePath))
        {
            error("DFG: 統合用Music Typeの外部パッチを読むpackage.dbがありません");
            return;
        }

        var patches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(packageDatabasePath),
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT source_plugin, local_form_id, form_kind, changes_json
                FROM external_patches
                WHERE form_kind = 'MusicType';
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = CreateDfgPatchIdentity(
                    reader.GetString(0),
                    checked((uint)reader.GetInt64(1)),
                    reader.GetString(2));
                patches[key] = reader.GetString(3);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error($"DFG: 統合用Music Typeの外部パッチを検証できません（{exception.Message}）");
            return;
        }

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseFormKey(target.FormKey, out var formKey))
            {
                error($"DFG: 統合用Music TypeのFormIDを解釈できません：{target.FormKey}");
                continue;
            }

            var identity = CreateDfgPatchIdentity(
                formKey.ModKey.FileName.String,
                checked((uint)formKey.ID),
                "MusicType");
            if (!patches.TryGetValue(identity, out var changesJson))
            {
                error($"{target.Label}: DFG外部パッチがありません");
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(changesJson);
                var root = document.RootElement;
                var tracks = root.GetProperty("fields")
                    .GetProperty("musicTypeTracks");
                var operation = tracks.GetProperty("operation").GetString();
                var value = tracks.GetProperty("value");
                if (!string.Equals(operation, "replace", StringComparison.OrdinalIgnoreCase) ||
                    value.ValueKind != JsonValueKind.Array)
                {
                    error($"{target.Label}: DFG外部パッチがTrack一覧置換になっていません");
                    continue;
                }

                var hasDfgReference = value.EnumerateArray().Any(reference =>
                    reference.TryGetProperty("editorID", out var editorId) &&
                    editorId.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(editorId.GetString()) &&
                    !reference.TryGetProperty("formID", out _));
                if (!hasDfgReference)
                {
                    error($"{target.Label}: DFG Music TrackのEditorID参照がありません");
                    continue;
                }

                check($"{target.Label}: DFG外部パッチがTrack一覧を置換");
            }
            catch (Exception exception)
            {
                error($"{target.Label}: DFG外部パッチの内容を検証できません（{exception.Message}）");
            }
        }
    }

    private static string CreateDfgPatchIdentity(
        string sourcePlugin,
        uint localFormId,
        string formKind) =>
        $"{sourcePlugin}\u001f{localFormId:X8}\u001f{formKind}";

    private static void ValidateTracks(
        IReadOnlyList<GeneratedMusicTrackOutput> tracks,
        IReadOnlyDictionary<string, IReadOnlyList<PluginRecordSource>> recordsByPlugin,
        Action<string> check,
        Action<string> error,
        CancellationToken cancellationToken)
    {
        foreach (var expected in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!recordsByPlugin.TryGetValue(expected.PluginFileName, out var records))
            {
                error($"Music Track {expected.FormKey}: its ESP was not readable");
                continue;
            }

            var record = FindRecord(records, expected.FormKey, "MusicTrack");
            if (record is null)
            {
                error($"Music Track {expected.FormKey}: generated record was not found");
                continue;
            }

            var trackAsset = record.Assets.FirstOrDefault(asset =>
                asset.FieldName.Equals("TrackFilename", StringComparison.OrdinalIgnoreCase));
            if (trackAsset is null ||
                !NormalizeVirtualPath(trackAsset.VirtualPath).Equals(
                    NormalizeVirtualPath(expected.VirtualPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                error(
                    $"Music Track {expected.FormKey}: TrackFilename does not match {expected.VirtualPath}");
                continue;
            }

            check($"Music Track {expected.FormKey}: record and audio path match");

            var expectedConditionKeys = expected.Conditions
                .Select(MusicConditionFormatter.CreateRecordKey)
                .ToArray();
            var actualConditions = record.Conditions
                .Select(condition => MusicConditionSource.From(condition))
                .ToArray();
            var actualConditionKeys = actualConditions
                .Select(MusicConditionFormatter.CreateRecordKey)
                .ToArray();
            if (!expectedConditionKeys.SequenceEqual(
                    actualConditionKeys,
                    StringComparer.OrdinalIgnoreCase))
            {
                error(
                    $"Music Track {expected.FormKey}: 再生条件が生成予定と一致しません " +
                    $"(予定 {expectedConditionKeys.Length}件 / 生成 {actualConditionKeys.Length}件)");
                continue;
            }

            check($"Music Track {expected.FormKey}: 再生条件{actualConditionKeys.Length}件が一致");
        }
    }

    private static void ValidateWorldSpaces(
        IReadOnlyList<GeneratedWorldSpaceOutput> worldSpaces,
        IReadOnlyDictionary<string, IReadOnlyList<PluginRecordSource>> recordsByPlugin,
        Action<string> check,
        Action<string> error,
        CancellationToken cancellationToken,
        bool requireEmptyStaticTracks)
    {
        foreach (var expected in worldSpaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!recordsByPlugin.TryGetValue(expected.PluginFileName, out var records))
            {
                error($"WorldSpace {expected.WorldSpaceFormKey}: its ESP was not readable");
                continue;
            }

            var musicType = FindRecord(records, expected.MusicTypeFormKey, "MusicType");
            if (musicType is null)
            {
                error($"WorldSpace {expected.WorldSpaceFormKey}: generated Music Type was not found");
            }
            else
            {
                var registeredTracks = musicType.References
                    .Where(reference => reference.FieldName.Equals("Tracks", StringComparison.OrdinalIgnoreCase))
                    .Select(reference => reference.FormKey)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (requireEmptyStaticTracks)
                {
                    if (registeredTracks.Count == 0)
                    {
                        check(
                            $"WorldSpace {expected.WorldSpaceFormKey}: " +
                            "DFG橋渡し用Music Typeの静的Trackは0件");
                    }
                    else
                    {
                        error(
                            $"WorldSpace {expected.WorldSpaceFormKey}: " +
                            $"DFG橋渡し用Music Typeに静的Trackが{registeredTracks.Count}件あります");
                    }
                }
                var missingTracks = expected.TrackFormKeys
                    .Where(track => !registeredTracks.Contains(track))
                    .ToArray();
                if (missingTracks.Length > 0)
                {
                    error(
                        $"WorldSpace {expected.WorldSpaceFormKey}: Music Type is missing {missingTracks.Length} track reference(s)");
                }
                else
                {
                    check($"WorldSpace {expected.WorldSpaceFormKey}: Music Type track references match");
                }
            }

            var worldSpace = FindRecord(records, expected.WorldSpaceFormKey, "Worldspace");
            if (worldSpace is null)
            {
                error($"WorldSpace {expected.WorldSpaceFormKey}: override record was not found");
                continue;
            }

            var musicReference = worldSpace.References.FirstOrDefault(reference =>
                reference.FieldName.Equals("Music", StringComparison.OrdinalIgnoreCase));
            if (musicReference is null ||
                !musicReference.FormKey.Equals(expected.MusicTypeFormKey, StringComparison.OrdinalIgnoreCase))
            {
                error($"WorldSpace {expected.WorldSpaceFormKey}: Music reference does not point to the generated Music Type");
            }
            else
            {
                check($"WorldSpace {expected.WorldSpaceFormKey}: override points to generated Music Type");
            }
        }
    }

    private static void ValidateIntegratedMusicTypes(
        IReadOnlyList<GeneratedMusicTypeOutput> integratedMusicTypes,
        IReadOnlyDictionary<string, IReadOnlyList<PluginRecordSource>> recordsByPlugin,
        Action<string> check,
        Action<string> error,
        CancellationToken cancellationToken,
        bool requireEmptyStaticTracks)
    {
        foreach (var expected in integratedMusicTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!recordsByPlugin.TryGetValue(
                    expected.PluginFileName,
                    out var records))
            {
                error(
                    $"統合用Music Type {expected.MusicTypeEditorId}: 生成ESPを読み取れません");
                continue;
            }

            var musicType = FindRecord(
                records,
                expected.MusicTypeFormKey,
                "MusicType");
            if (musicType is null)
            {
                error(
                    $"統合用Music Type {expected.MusicTypeEditorId}: レコードが見つかりません");
                continue;
            }

            var registeredTracks = musicType.References
                .Where(reference => reference.FieldName.Equals(
                    "Tracks",
                    StringComparison.OrdinalIgnoreCase))
                .Select(reference => reference.FormKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var expectedTracks = expected.TrackFormKeys
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (requireEmptyStaticTracks)
            {
                if (registeredTracks.Count == 0)
                {
                    check(
                        $"統合用Music Type {expected.MusicTypeEditorId}: " +
                        "DFG橋渡し用の静的Trackは0件");
                }
                else
                {
                    error(
                        $"統合用Music Type {expected.MusicTypeEditorId}: " +
                        $"DFG橋渡し用Music Typeに静的Trackが{registeredTracks.Count}件あります");
                }
            }
            if (!registeredTracks.SetEquals(expectedTracks))
            {
                error(
                    $"統合用Music Type {expected.MusicTypeEditorId}: Track一覧が生成予定と一致しません " +
                    $"(予定 {expectedTracks.Count}件 / 生成 {registeredTracks.Count}件)");
                continue;
            }

            check(
                $"統合用Music Type {expected.MusicTypeEditorId}: " +
                $"{expected.Scope}のTrack一覧が一致");
        }
    }

    private static void ValidateCellSkyPatcher(
        string stageDirectory,
        string? cellSkyPatcherFileName,
        IReadOnlyList<GeneratedCellOutput> cells,
        Action<string> check,
        Action<string> error,
        CancellationToken cancellationToken)
    {
        if (cells.Count == 0)
        {
            if (cellSkyPatcherFileName is not null)
            {
                error("Cell用SkyPatcher設定: 対象Cellがないのに出力ファイルが指定されています");
            }
            else
            {
                check("Cell用SkyPatcher設定: 割り当てなし");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(cellSkyPatcherFileName))
        {
            error("Cell用SkyPatcher設定: 出力ファイルが指定されていません");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(
            stageDirectory,
            cellSkyPatcherFileName.Replace('\\', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            error($"Cell用SkyPatcher設定: 出力ファイルがありません ({cellSkyPatcherFileName})");
            return;
        }

        try
        {
            var actualPatchLines = File.ReadAllLines(path)
                .Where(line => line.TrimStart().StartsWith(
                    "filterByCells=",
                    StringComparison.OrdinalIgnoreCase))
                .Select(line => line.Trim())
                .ToArray();
            var expectedPatchLines = MusicCellSkyPatcherOutput.BuildIni(cells)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith(
                    "filterByCells=",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (!expectedPatchLines.SequenceEqual(
                    actualPatchLines,
                    StringComparer.OrdinalIgnoreCase))
            {
                error(
                    $"Cell用SkyPatcher設定: Cell割り当てが生成予定と一致しません " +
                    $"(予定 {expectedPatchLines.Length}件 / 生成 {actualPatchLines.Length}件)");
                return;
            }

            check($"Cell用SkyPatcher設定: {actualPatchLines.Length}件の割り当てが一致");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error($"Cell用SkyPatcher設定: 出力ファイルを検証できません ({exception.Message})");
        }
    }

    private static void ValidateMtd(
        string mtdFilePath,
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyList<GeneratedMusicTrackOutput> tracks,
        IReadOnlyList<GeneratedWorldSpaceOutput> worldSpaces,
        IReadOnlyList<GeneratedMusicTypeOutput> integratedMusicTypes,
        bool worldSpaceIndividualAssignment,
        MusicGenerationPlanResolution? planResolution,
        MusicGenerationOutputMode outputMode,
        Action<string> check,
        Action<string> error,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(mtdFilePath))
        {
            return;
        }

        string[] mtdLines;
        Dictionary<string, Dictionary<string, string>> sections;
        try
        {
            mtdLines = File.ReadAllLines(mtdFilePath);
            sections = ParseMtd(mtdLines);
        }
        catch (Exception exception)
        {
            error($"MTD: could not be read ({exception.Message})");
            return;
        }

        if (planResolution is not null)
        {
            if (outputMode != MusicGenerationOutputMode.Dfg)
            {
                ValidateResolvedTypeTrackLists(
                mtdLines,
                sections,
                planResolution,
                settings,
                tracks,
                worldSpaces,
                worldSpaceIndividualAssignment,
                check,
                error);
            }
        }

        var settingsByKey = settings
            .GroupBy(CreateDestinationKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                SelectRepresentativeSetting,
                StringComparer.OrdinalIgnoreCase);
        var selectedWorldSpaces = worldSpaces
            .Select(worldSpace => worldSpace.WorldSpaceFormKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var integratedByTarget = integratedMusicTypes.ToDictionary(
            output => output.TargetKey,
            StringComparer.OrdinalIgnoreCase);
        var checkedMtdEntries = 0;
        var hasApplicableMtdEntry = false;

        if (outputMode == MusicGenerationOutputMode.Dfg &&
            planResolution is not null)
        {
            checkedMtdEntries += ValidateDfgMtdScopeMappings(
                sections,
                settings,
                planResolution,
                integratedByTarget,
                check,
                error);
            hasApplicableMtdEntry = checkedMtdEntries > 0;
        }

        foreach (var track in tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedTrackId = FormatMtdFormId(track.FormKey);
            foreach (var destinationKey in track.DestinationKeys)
            {
                if (!settingsByKey.TryGetValue(destinationKey, out var setting))
                {
                    error($"MTD: destination definition could not be resolved ({destinationKey})");
                    continue;
                }

                switch (setting.Scope)
                {
                    case MusicSettingScope.Cell:
                        // Cell is validated separately against the generated
                        // SkyPatcher assignment file.
                        continue;
                    case MusicSettingScope.WorldSpace when
                        worldSpaceIndividualAssignment &&
                        selectedWorldSpaces.Contains(setting.ScopeFormKey):
                        continue;
                }

                if (planResolution is not null &&
                    planResolution.TryGetIntegrationTarget(
                        setting.Scope,
                        setting.ScopeFormKey,
                        out var integrationTarget) &&
                    integratedByTarget.TryGetValue(
                        integrationTarget.TargetKey,
                        out var integratedMusicType))
                {
                    hasApplicableMtdEntry = true;
                    if (!integratedMusicType.TrackFormKeys.Contains(
                            track.FormKey,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        error(
                            $"MTD: integrated Music Type {integratedMusicType.MusicTypeEditorId} " +
                            $"does not contain {track.FormKey}");
                        continue;
                    }

                    if (setting.Scope is MusicSettingScope.Location or MusicSettingScope.Region &&
                        !HasScopeMapping(
                            sections,
                            setting.Scope,
                            setting.ScopeEditorId,
                            integratedMusicType.MusicTypeEditorId))
                    {
                        error(
                            $"MTD: {setting.Scope} {setting.ScopeEditorId} does not map to " +
                            $"integrated Music Type {integratedMusicType.MusicTypeEditorId}");
                        continue;
                    }

                    checkedMtdEntries++;
                    continue;
                }

                if (outputMode == MusicGenerationOutputMode.Dfg)
                {
                    if (setting.Scope is MusicSettingScope.Location or MusicSettingScope.Region &&
                        !HasScopeMapping(
                            sections,
                            setting.Scope,
                            setting.ScopeEditorId,
                            setting.MusicTypeEditorId))
                    {
                        error(
                            $"MTD: {setting.Scope} {setting.ScopeEditorId} does not map to " +
                            $"Music Type {setting.MusicTypeEditorId}");
                        continue;
                    }

                    hasApplicableMtdEntry = true;
                    checkedMtdEntries++;
                    continue;
                }

                hasApplicableMtdEntry = true;
                if (!HasGeneralTrack(sections, setting.MusicTypeEditorId, expectedTrackId))
                {
                    error(
                        $"MTD: {track.FormKey} is not registered under Music Type {setting.MusicTypeEditorId}");
                    continue;
                }

                if (setting.Scope is MusicSettingScope.Location or MusicSettingScope.Region &&
                    !HasScopeMapping(sections, setting.Scope, setting.ScopeEditorId, setting.MusicTypeEditorId))
                {
                    error(
                        $"MTD: {setting.Scope} {setting.ScopeEditorId} does not map to Music Type {setting.MusicTypeEditorId}");
                    continue;
                }

                checkedMtdEntries++;
            }
        }

        if (checkedMtdEntries > 0)
        {
            check($"MTD: {checkedMtdEntries} generated assignment(s) point to the expected Music Track");
        }
        else if (hasApplicableMtdEntry)
        {
            error("MTD: no applicable Music Track assignment could be verified");
        }
        else
        {
            check("MTD: no MTD assignment was required");
        }
    }

    private static int ValidateDfgMtdScopeMappings(
        IReadOnlyDictionary<string, Dictionary<string, string>> sections,
        IReadOnlyList<MusicSettingSource> settings,
        MusicGenerationPlanResolution planResolution,
        IReadOnlyDictionary<string, GeneratedMusicTypeOutput> integratedByTarget,
        Action<string> check,
        Action<string> error)
    {
        var settingsByKey = settings
            .GroupBy(CreateDestinationKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                SelectRepresentativeSetting,
                StringComparer.OrdinalIgnoreCase);
        var checkedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedCount = 0;
        foreach (var entry in planResolution.MusicTypes
                     .SelectMany(musicType => musicType.GeneratedEntries)
                     .DistinctBy(entry => entry.AssetKey, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var destination in entry.DestinationKeys)
            {
                if (destination.Scope is not MusicSettingScope.Location and
                    not MusicSettingScope.Region)
                {
                    continue;
                }

                var destinationKey = CreateDestinationKey(destination);
                if (!settingsByKey.TryGetValue(destinationKey, out var setting))
                {
                    error($"MTD: 定義元を解決できません（{destinationKey}）");
                    continue;
                }

                var finalMusicTypeEditorId = setting.MusicTypeEditorId;
                if (planResolution.TryGetIntegrationTarget(
                        destination.Scope,
                        destination.ScopeFormKey,
                        out var integrationTarget) &&
                    integratedByTarget.TryGetValue(
                        integrationTarget.TargetKey,
                        out var integratedMusicType))
                {
                    finalMusicTypeEditorId = integratedMusicType.MusicTypeEditorId;
                }

                var mappingKey = string.Join(
                    "\u001f",
                    destination.Scope,
                    setting.ScopeEditorId ?? setting.ScopeFormKey,
                    finalMusicTypeEditorId ?? string.Empty);
                if (!checkedKeys.Add(mappingKey))
                {
                    continue;
                }

                if (!HasScopeMapping(
                        sections,
                        destination.Scope,
                        setting.ScopeEditorId,
                        finalMusicTypeEditorId))
                {
                    error(
                        $"MTD: {destination.Scope} {setting.ScopeName} does not map to " +
                        $"Music Type {finalMusicTypeEditorId ?? "(未定義)"}");
                    continue;
                }

                checkedCount++;
                check(
                    $"MTD: DFG方式の{destination.Scope} {setting.ScopeName}が" +
                    $"Music Type {finalMusicTypeEditorId}を参照");
            }
        }

        return checkedCount;
    }

    private static void ValidateResolvedTypeTrackLists(
        IReadOnlyList<string> mtdLines,
        IReadOnlyDictionary<string, Dictionary<string, string>> sections,
        MusicGenerationPlanResolution planResolution,
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyList<GeneratedMusicTrackOutput> generatedTracks,
        IReadOnlyList<GeneratedWorldSpaceOutput> worldSpaces,
        bool worldSpaceIndividualAssignment,
        Action<string> check,
        Action<string> error)
    {
        var replacementKeys = ParseReplacementGeneralKeys(mtdLines);
        var generatedByAssetKey = generatedTracks
            .GroupBy(track => track.AssetKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var selectedWorldSpaces = worldSpaces
            .Select(worldSpace => worldSpace.WorldSpaceFormKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var adoptedEntries = planResolution.MusicTypes
            .SelectMany(musicType => musicType.GeneratedEntries)
            .DistinctBy(entry => entry.AssetKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mtdAggregation = MusicTypeMtdAggregator.Build(
            planResolution,
            adoptedEntries,
            settings,
            worldSpaceIndividualAssignment,
            selectedWorldSpaces);

        foreach (var musicTypeFormKey in mtdAggregation.MissingDefinitionFormKeys)
        {
            error($"MTD: Music Type definition was not found ({musicTypeFormKey})");
        }

        foreach (var musicTypeFormKey in mtdAggregation.MissingEditorIdFormKeys)
        {
            error($"MTD: Music Type EditorID is missing ({musicTypeFormKey})");
        }

        foreach (var aggregate in mtdAggregation.Aggregates)
        {
            var editorId = aggregate.MusicTypeEditorId;

            if (!replacementKeys.Contains(editorId))
            {
                error($"MTD: Music Type {editorId} is not written in replacement form");
                continue;
            }

            var expectedTrackIds = aggregate.OfficialTrackFormKeys
                .Select(FormatMtdFormId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missingGeneratedTrack = false;
            foreach (var generatedEntry in aggregate.GeneratedEntries)
            {
                if (!generatedByAssetKey.TryGetValue(generatedEntry.AssetKey, out var generated))
                {
                    error($"MTD: generated Music Track was not found ({generatedEntry.AssetKey})");
                    missingGeneratedTrack = true;
                    continue;
                }

                foreach (var generatedTrack in generated)
                {
                    expectedTrackIds.Add(FormatMtdFormId(generatedTrack.FormKey));
                }
            }

            if (missingGeneratedTrack)
            {
                continue;
            }
            if (!sections.TryGetValue("General", out var general) ||
                !general.TryGetValue(editorId, out var actualValue))
            {
                error($"MTD: Music Type {editorId} has no final Track list");
                continue;
            }

            var actualTrackIds = actualValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!actualTrackIds.SetEquals(expectedTrackIds))
            {
                error(
                    $"MTD: Music Type {editorId} final Track list does not match " +
                    $"(expected={expectedTrackIds.Count}, actual={actualTrackIds.Count})");
                continue;
            }

            check($"MTD: Music Type {editorId} uses the expected replacement Track list");
        }
    }

    private static IReadOnlySet<string> ParseReplacementGeneralKeys(
        IEnumerable<string> lines)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inGeneralSection = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inGeneralSection = trimmed[1..^1].Trim().Equals(
                    "General",
                    StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inGeneralSection)
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator < 1)
            {
                continue;
            }

            var key = trimmed[..separator].Trim();
            if (key.EndsWith('!'))
            {
                keys.Add(key[..^1].Trim());
            }
        }

        return keys;
    }

    private static MusicSettingSource SelectRepresentativeSetting(
        IEnumerable<MusicSettingSource> candidates) =>
        MusicTypeMtdAggregator.SelectRepresentativeSetting(candidates);

    private void ValidateAssets(
        string stageDirectory,
        IReadOnlyList<GeneratedAssetOutput> assets,
        Action<string> check,
        Action<string> error,
        CancellationToken cancellationToken)
    {
        foreach (var asset in assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (asset.IsCopied)
            {
                if (string.IsNullOrWhiteSpace(asset.OutputPath))
                {
                    error($"音源 {asset.VirtualPath}: copied asset has no output path");
                    continue;
                }

                if (!TryResolveStagePath(stageDirectory, asset.OutputPath, out var outputPath))
                {
                    error($"音源 {asset.VirtualPath}: output path escapes the generated MOD");
                    continue;
                }

                if (!File.Exists(outputPath))
                {
                    error($"音源 {asset.VirtualPath}: copied file is missing");
                    continue;
                }

                var outputLength = new FileInfo(outputPath).Length;
                if (outputLength != asset.Length)
                {
                    error(
                        $"音源 {asset.VirtualPath}: copied file length mismatch (expected {asset.Length}, actual {outputLength})");
                    continue;
                }

                check($"音源 {asset.VirtualPath}: copied file exists");
                continue;
            }

            if (asset.SourceKind == AssetSourceKind.Loose)
            {
                if (!File.Exists(asset.SourcePath))
                {
                    error($"音源 {asset.VirtualPath}: referenced loose source is missing");
                    continue;
                }

                check($"音源 {asset.VirtualPath}: referenced loose source exists");
                continue;
            }

            try
            {
                var archive = _archiveReader.ReadIndex(asset.SourcePath);
                var entryPath = NormalizeVirtualPath(asset.ArchiveEntryPath ?? asset.VirtualPath);
                if (!archive.Entries.Any(entry =>
                        NormalizeVirtualPath(entry.VirtualPath).Equals(
                            entryPath,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    error($"音源 {asset.VirtualPath}: referenced BSA entry is missing");
                    continue;
                }

                check($"音源 {asset.VirtualPath}: referenced BSA entry exists");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                error($"音源 {asset.VirtualPath}: referenced BSA could not be read ({exception.Message})");
            }
        }
    }

    private static void ValidateManifest(
        string manifestPath,
        string outputDirectory,
        string mtdFileName,
        bool worldSpaceIndividualAssignment,
        IReadOnlyList<GeneratedPluginOutput> plugins,
        IReadOnlyList<GeneratedMusicTrackOutput> tracks,
        IReadOnlyList<GeneratedWorldSpaceOutput> worldSpaces,
        IReadOnlyList<GeneratedCellOutput> cells,
        IReadOnlyList<GeneratedMusicTypeOutput> integratedMusicTypes,
        string? cellSkyPatcherFileName,
        IReadOnlyList<GeneratedAssetOutput> assets,
        int expectedNewRecordCount,
        int maxNewRecordsPerPlugin,
        Action<string> check,
        Action<string> error,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            ValidateManifestInt(root, "SchemaVersion", 5, error);
            ValidateManifestString(root, "OutputModDirectory", outputDirectory, error);
            ValidateManifestText(root, "MtdFileName", mtdFileName, error);
            ValidateManifestBoolean(
                root,
                "WorldSpaceIndividualAssignment",
                worldSpaceIndividualAssignment,
                error);
            ValidateManifestArrayCount(root, "Plugins", plugins.Count, error);
            ValidateManifestArrayCount(root, "Tracks", tracks.Count, error);
            ValidateManifestArrayCount(root, "WorldSpaces", worldSpaces.Count, error);
            ValidateManifestArrayCount(root, "Cells", cells.Count, error);
            ValidateManifestArrayCount(
                root,
                "IntegratedMusicTypes",
                integratedMusicTypes.Count,
                error);
            ValidateManifestNullableText(root, "CellSkyPatcherFileName", cellSkyPatcherFileName, error);
            ValidateManifestArrayCount(root, "Assets", assets.Count, error);
            // One plan entry belongs to one audio source.  A source may now
            // produce several Music Track records, so the Track count is not
            // the expected PlanEntries count.
            ValidateManifestMinimumArrayCount(root, "PlanEntries", assets.Count, error);
            ValidateManifestInt(root, "NewRecordCount", expectedNewRecordCount, error);
            ValidateManifestInt(root, "MaxNewRecordsPerPlugin", maxNewRecordsPerPlugin, error);
            check("manifest: schema and generated counts match");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error($"manifest: could not be validated ({exception.Message})");
        }
    }

    private static void ValidateManifestText(
        JsonElement root,
        string propertyName,
        string expected,
        Action<string> error)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !string.Equals(
                property.GetString(),
                expected,
                StringComparison.OrdinalIgnoreCase))
        {
            error($"manifest: {propertyName} does not match the generated output");
        }
    }

    private static void ValidateManifestNullableText(
        JsonElement root,
        string propertyName,
        string? expected,
        Action<string> error)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            error($"manifest: {propertyName} is missing");
            return;
        }

        if (expected is null)
        {
            if (property.ValueKind != JsonValueKind.Null)
            {
                error($"manifest: {propertyName} does not match the generated output");
            }

            return;
        }

        if (property.ValueKind != JsonValueKind.String ||
            !string.Equals(property.GetString(), expected, StringComparison.OrdinalIgnoreCase))
        {
            error($"manifest: {propertyName} does not match the generated output");
        }
    }

    private static void ValidateManifestString(
        JsonElement root,
        string propertyName,
        string expected,
        Action<string> error)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            !Path.GetFullPath(property.GetString() ?? string.Empty).Equals(
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase))
        {
            error($"manifest: {propertyName} does not match the generated output");
        }
    }

    private static void ValidateManifestBoolean(
        JsonElement root,
        string propertyName,
        bool expected,
        Action<string> error)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.True &&
            property.ValueKind != JsonValueKind.False ||
            property.GetBoolean() != expected)
        {
            error($"manifest: {propertyName} does not match the generated output");
        }
    }

    private static void ValidateManifestArrayCount(
        JsonElement root,
        string propertyName,
        int expected,
        Action<string> error)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array ||
            property.GetArrayLength() != expected)
        {
            error($"manifest: {propertyName} count does not match the generated output");
        }
    }

    private static void ValidateManifestMinimumArrayCount(
        JsonElement root,
        string propertyName,
        int minimum,
        Action<string> error)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array ||
            property.GetArrayLength() < minimum)
        {
            error($"manifest: {propertyName} does not contain the generated edit state");
        }
    }

    private static void ValidateManifestInt(
        JsonElement root,
        string propertyName,
        int expected,
        Action<string> error)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var actual) ||
            actual != expected)
        {
            error($"manifest: {propertyName} does not match the generated output");
        }
    }

    private static void ValidateCount(
        string label,
        int actual,
        int expected,
        Action<string> check,
        Action<string> error)
    {
        if (actual == expected)
        {
            check($"{label}: record count {actual}");
        }
        else
        {
            error($"{label}: expected {expected} record(s), found {actual}");
        }
    }

    private static int CountRecords(
        IReadOnlyList<PluginRecordSource> records,
        string recordType) =>
        records.Count(record => record.RecordType.Equals(recordType, StringComparison.OrdinalIgnoreCase));

    private static PluginRecordSource? FindRecord(
        IReadOnlyList<PluginRecordSource> records,
        string formKey,
        string recordType) =>
        records.FirstOrDefault(record =>
            record.RecordType.Equals(recordType, StringComparison.OrdinalIgnoreCase) &&
            record.FormKey.Equals(formKey, StringComparison.OrdinalIgnoreCase));

    private static bool HasGeneralTrack(
        IReadOnlyDictionary<string, Dictionary<string, string>> sections,
        string? musicTypeEditorId,
        string expectedTrack)
    {
        if (string.IsNullOrWhiteSpace(musicTypeEditorId) ||
            !sections.TryGetValue("General", out var general) ||
            !general.TryGetValue(musicTypeEditorId, out var value))
        {
            return false;
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(track => track.Equals(expectedTrack, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasScopeMapping(
        IReadOnlyDictionary<string, Dictionary<string, string>> sections,
        MusicSettingScope scope,
        string? scopeEditorId,
        string? musicTypeEditorId)
    {
        var sectionName = scope == MusicSettingScope.Location ? "Location" : "Region";
        return !string.IsNullOrWhiteSpace(scopeEditorId) &&
               !string.IsNullOrWhiteSpace(musicTypeEditorId) &&
               sections.TryGetValue(sectionName, out var section) &&
               section.TryGetValue(scopeEditorId, out var value) &&
               value.Equals(musicTypeEditorId, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, Dictionary<string, string>> ParseMtd(
        IEnumerable<string> lines)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                var sectionName = trimmed[1..^1].Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[sectionName] = current;
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (current is null || separator < 1)
            {
                continue;
            }

            var key = trimmed[..separator].Trim().TrimEnd('!');
            var value = trimmed[(separator + 1)..].Trim();
            current[key] = value;
        }

        return sections;
    }

    private static string CreateDestinationKey(MusicSettingSource setting) => string.Join(
        "\u001f",
        setting.Scope,
        setting.ScopeFormKey,
        setting.MusicTypeFormKey);

    private static string CreateDestinationKey(MusicSettingKey setting) => string.Join(
        "\u001f",
        setting.Scope,
        setting.ScopeFormKey,
        setting.MusicTypeFormKey);

    private static string FormatMtdFormId(string formKey)
    {
        if (!TryParseFormKey(formKey, out var parsed))
        {
            return formKey;
        }

        return $"0x{parsed.ID:X6}~{parsed.ModKey.FileName}";
    }

    private static bool TryParseFormKey(string value, out FormKey formKey)
    {
        try
        {
            formKey = FormKey.Factory(value);
            return true;
        }
        catch
        {
            formKey = default;
            return false;
        }
    }

    private static string NormalizeVirtualPath(string path) => path
        .Replace('/', '\\')
        .TrimStart('\\')
        .ToLowerInvariant();

    private static bool TryResolveStagePath(
        string stageDirectory,
        string relativePath,
        out string fullPath)
    {
        fullPath = string.Empty;
        var normalized = relativePath.Replace('\\', Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(stageDirectory, normalized));
        var root = Path.GetFullPath(stageDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }
}
