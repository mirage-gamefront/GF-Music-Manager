using System.Text.Json;
using GfMusicManager.Application;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Generation;
using SkyrimScan.Core.Models;

Console.OutputEncoding = System.Text.Encoding.UTF8;

try
{
    if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
    {
        PrintHelp();
        return 0;
    }

    if (args[0].Equals("plan", StringComparison.OrdinalIgnoreCase) &&
        args.Length >= 2 &&
        args[1].Equals("create", StringComparison.OrdinalIgnoreCase))
    {
        var parsedPlan = ParsePlanCreateOptions(args[2..]);
        var scanDocument = MusicScanResultJson.Load(parsedPlan.ScanResultPath);
        var planService = new MusicPlanApplicationService();
        var plan = planService.CreatePlan(scanDocument.Result);
        var defaultPathConflictChanges = planService.ApplyDefaultPathConflictSelection(
            plan,
            scanDocument.Result.AudioDuplicates,
            scanDocument.Result.Scan.Mods);
        if (parsedPlan.KeepVanillaMusic.HasValue)
        {
            plan.KeepVanillaMusic = parsedPlan.KeepVanillaMusic;
        }

        var snapshot = planService.Capture(plan);
        MusicPlanJson.Save(parsedPlan.OutputPath, snapshot);

        var planSummary = new
        {
            command = "plan create",
            input = Path.GetFullPath(parsedPlan.ScanResultPath),
            output = Path.GetFullPath(parsedPlan.OutputPath),
            entries = snapshot.Entries.Count,
            adoptedEntries = snapshot.Entries.Count(entry => entry.IsAdopted),
            tracks = snapshot.Entries.Sum(entry => entry.Tracks.Count),
            conflicts = plan.Conflicts.Count,
            defaultPathConflictChanges,
            keepVanillaMusic = snapshot.KeepVanillaMusic
        };
        Console.WriteLine(JsonSerializer.Serialize(planSummary, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    if (args[0].Equals("plan", StringComparison.OrdinalIgnoreCase) &&
        args.Length >= 2 &&
        args[1].Equals("edit", StringComparison.OrdinalIgnoreCase))
    {
        var parsedEdit = ParsePlanEditOptions(args[2..]);
        var planDocument = MusicPlanJson.Load(parsedEdit.PlanPath);
        var editedPlan = new MusicPlanApplicationService().Edit(
            planDocument.Plan,
            parsedEdit.AdoptAssetKeys,
            parsedEdit.ExcludeAssetKeys,
            parsedEdit.KeepVanillaMusic);
        MusicPlanJson.Save(parsedEdit.OutputPath, editedPlan);
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                command = "plan edit",
                input = Path.GetFullPath(parsedEdit.PlanPath),
                output = Path.GetFullPath(parsedEdit.OutputPath),
                entries = editedPlan.Entries.Count,
                adoptedEntries = editedPlan.Entries.Count(entry => entry.IsAdopted),
                tracks = editedPlan.Entries.Sum(entry => entry.Tracks.Count),
                adopted = parsedEdit.AdoptAssetKeys.Count,
                excluded = parsedEdit.ExcludeAssetKeys.Count,
                keepVanillaMusic = editedPlan.KeepVanillaMusic
            },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    if (args[0].Equals("plan", StringComparison.OrdinalIgnoreCase) &&
        args.Length >= 2 &&
        args[1].Equals("validate", StringComparison.OrdinalIgnoreCase))
    {
        var parsedValidation = ParseGenerationOptions(args[2..]);
        var scanDocument = MusicScanResultJson.Load(parsedValidation.ScanResultPath);
        var planDocument = MusicPlanJson.Load(parsedValidation.PlanPath);
        var planSnapshot = ApplyVanillaOverride(
            planDocument.Plan,
            parsedValidation.KeepVanillaMusic);
        var validation = new MusicGenerationApplicationService().Validate(
            scanDocument.Result,
            planSnapshot,
            parsedValidation.Options);
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                command = "plan validate",
                input = Path.GetFullPath(parsedValidation.ScanResultPath),
                plan = Path.GetFullPath(parsedValidation.PlanPath),
                output = validation.OutputModDirectory,
                valid = validation.IsValid,
                entries = validation.EntryCount,
                adoptedEntries = validation.AdoptedEntryCount,
                tracks = validation.TrackCount,
                integrationTargets = validation.IntegrationTargetCount,
                conflicts = validation.ConflictCount,
                errors = validation.Errors,
                warnings = validation.Warnings
            },
            new JsonSerializerOptions { WriteIndented = true }));
        return validation.IsValid ? 0 : 20;
    }

    if (args[0].Equals("report", StringComparison.OrdinalIgnoreCase))
    {
        var parsedReport = ParseReportOptions(args[1..]);
        var scanDocument = MusicScanResultJson.Load(parsedReport.ScanResultPath);
        var reportResult = new MusicReportingApplicationService().Write(
            scanDocument.Result,
            parsedReport.OutputDirectory,
            parsedReport.FileStem,
            parsedReport.LongPlacementThreshold);
        var files = reportResult.Files;
        var report = reportResult.Report;
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                command = "report",
                scanResult = Path.GetFullPath(parsedReport.ScanResultPath),
                outputDirectory = Path.GetFullPath(parsedReport.OutputDirectory),
                json = files.JsonPath,
                tsv = files.TsvPath,
                recordsTsv = files.RecordsTsvPath,
                contextRecordsTsv = files.ContextRecordsTsvPath,
                assets = report.AssetCount,
                mapped = report.MappedAssetCount,
                settings = report.MusicSettingCount,
                records = report.Records.Count,
                issues = report.Issues.Count
            },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    if (args[0].Equals("generate", StringComparison.OrdinalIgnoreCase))
    {
        var parsedGeneration = ParseGenerationOptions(args[1..]);
        var scanDocument = MusicScanResultJson.Load(parsedGeneration.ScanResultPath);
        var planDocument = MusicPlanJson.Load(parsedGeneration.PlanPath);
        var planSnapshot = ApplyVanillaOverride(
            planDocument.Plan,
            parsedGeneration.KeepVanillaMusic);
        var generated = new MusicGenerationApplicationService().Generate(
            scanDocument.Result,
            planSnapshot,
            parsedGeneration.Options);
        var output = generated.Output;
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                command = "generate",
                scanResult = Path.GetFullPath(parsedGeneration.ScanResultPath),
                plan = Path.GetFullPath(parsedGeneration.PlanPath),
                output = output.OutputModDirectory,
                tracks = output.Tracks.Count,
                plugins = output.Plugins.Count,
                integratedMusicTypes = output.IntegratedMusicTypes.Count,
                worldSpaces = output.WorldSpaces.Count,
                cells = output.Cells.Count,
                copiedAssets = output.Assets.Count(asset => asset.IsCopied),
                referencedAssets = output.Assets.Count(asset => !asset.IsCopied),
                mtd = output.MtdFilePath,
                manifest = output.ManifestPath,
                diagnostic = new
                {
                    output.Diagnostic.IsSuccess,
                    output.Diagnostic.CheckCount,
                    output.Diagnostic.ErrorCount,
                    output.Diagnostic.Summary
                }
            },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    if (args[0].Equals("dfg", StringComparison.OrdinalIgnoreCase) &&
        args.Length >= 2 &&
        args[1].Equals("generate", StringComparison.OrdinalIgnoreCase))
    {
        var parsedDfg = ParseDfgGenerationOptions(args[2..]);
        var scanDocument = MusicScanResultJson.Load(parsedDfg.ScanResultPath);
        var planDocument = MusicPlanJson.Load(parsedDfg.PlanPath);
        var planSnapshot = ApplyVanillaOverride(
            planDocument.Plan,
            parsedDfg.KeepVanillaMusic);
        var generated = new MusicGenerationApplicationService().Generate(
            scanDocument.Result,
            planSnapshot,
            new MusicGenerationApplicationOptions
            {
                OutputModDirectory = parsedDfg.Options.OutputModDirectory,
                OutputMode = MusicGenerationOutputMode.Dfg,
                DfgPackageName = parsedDfg.Options.PackageName,
                OverwriteExisting = parsedDfg.Options.OverwriteExisting,
                WorldSpaceIndividualAssignment =
                    parsedDfg.Options.WorldSpaceIndividualAssignment,
                SelectedWorldSpaceFormKeys =
                    parsedDfg.Options.SelectedWorldSpaceFormKeys,
                ExistingMtdFileNames = parsedDfg.Options.ExistingMtdFileNames,
                CapacityPolicy = parsedDfg.Options.CapacityPolicy
            });
        var output = generated.Output;
        var dfgOutput = output.DfgOutput ??
            throw new InvalidOperationException(
                "DFG方式の生成結果が共通生成パイプラインから返されませんでした。");
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                command = "dfg generate",
                scanResult = Path.GetFullPath(parsedDfg.ScanResultPath),
                plan = Path.GetFullPath(parsedDfg.PlanPath),
                output = output.OutputModDirectory,
                outputMode = output.OutputMode.ToString(),
                package = dfgOutput.PackageDirectory,
                manifest = dfgOutput.ManifestPath,
                metadata = dfgOutput.MetadataPath,
                packageDatabase = dfgOutput.PackageDatabasePath,
                imports = dfgOutput.ImportPaths,
                musicTracks = dfgOutput.MusicTrackCount,
                musicTypes = dfgOutput.MusicTypeCount,
                externalMusicTypePatches = dfgOutput.ExternalMusicTypePatchCount,
                officialReferences = dfgOutput.OfficialReferenceCount,
                unsupportedAssignments = dfgOutput.UnsupportedAssignmentCount,
                commonOutput = new
                {
                    mtd = output.MtdFilePath,
                    cellSkyPatcher = output.CellSkyPatcherFilePath,
                    plugins = output.Plugins.Select(plugin => plugin.PluginFileName),
                    cells = output.Cells.Count,
                    worldSpaces = output.WorldSpaces.Count,
                    assets = output.Assets.Count,
                    referencedAssets = output.Assets.Count(asset => !asset.IsCopied),
                    copiedAssets = output.Assets.Count(asset => asset.IsCopied),
                    diagnostic = new
                    {
                        output.Diagnostic.IsSuccess,
                        output.Diagnostic.CheckCount,
                        output.Diagnostic.ErrorCount,
                        output.Diagnostic.Summary
                    }
                }
            },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    if (args[0].Equals("validate", StringComparison.OrdinalIgnoreCase))
    {
        var parsedValidation = ParseGenerationOptions(args[1..]);
        var scanDocument = MusicScanResultJson.Load(parsedValidation.ScanResultPath);
        var planDocument = MusicPlanJson.Load(parsedValidation.PlanPath);
        var planSnapshot = ApplyVanillaOverride(
            planDocument.Plan,
            parsedValidation.KeepVanillaMusic);
        var validation = new MusicGenerationApplicationService().ValidateOutput(
            scanDocument.Result,
            planSnapshot,
            parsedValidation.Options);
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                command = "validate",
                scanResult = Path.GetFullPath(parsedValidation.ScanResultPath),
                plan = Path.GetFullPath(parsedValidation.PlanPath),
                output = validation.OutputModDirectory,
                manifest = validation.ManifestPath,
                valid = validation.IsValid,
                diagnostic = new
                {
                    validation.Diagnostic.IsSuccess,
                    validation.Diagnostic.CheckCount,
                    validation.Diagnostic.ErrorCount,
                    validation.Diagnostic.Checks,
                    validation.Diagnostic.Errors,
                    validation.Diagnostic.Summary
                }
            },
            new JsonSerializerOptions { WriteIndented = true }));
        return validation.IsValid ? 0 : 30;
    }

    if (args[0].Equals("draft", StringComparison.OrdinalIgnoreCase) &&
        args.Length >= 2 &&
        args[1].Equals("reset", StringComparison.OrdinalIgnoreCase))
    {
        var parsedDraft = ParseDraftResetOptions(args[2..]);
        var draftService = new MusicDraftApplicationService();
        var path = parsedDraft.DraftPath ?? draftService.GetProfileDraftPath(
            parsedDraft.Mo2Root!,
            parsedDraft.ProfileName!);
        var deleted = draftService.Reset(path);
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                command = "draft reset",
                path = Path.GetFullPath(path),
                deleted
            },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    if (args[0].Equals("mo2", StringComparison.OrdinalIgnoreCase) &&
        args.Length >= 2 &&
        args[1].Equals("apply", StringComparison.OrdinalIgnoreCase))
    {
        var parsedApply = ParseMo2ApplyOptions(args[2..]);
        var scanDocument = MusicScanResultJson.Load(parsedApply.ScanResultPath);
        var planDocument = MusicPlanJson.Load(parsedApply.PlanPath);
        var planSnapshot = ApplyVanillaOverride(
            planDocument.Plan,
            parsedApply.KeepVanillaMusic);
        var validation = new MusicGenerationApplicationService().ValidateOutput(
            scanDocument.Result,
            planSnapshot,
            parsedApply.GenerationOptions);
        if (!validation.IsValid)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    command = "mo2 apply",
                    applied = false,
                    validation = new
                    {
                        validation.IsValid,
                        validation.Diagnostic.Errors,
                        validation.Diagnostic.Summary
                    }
                },
                new JsonSerializerOptions { WriteIndented = true }));
            return 30;
        }

        var manifest = JsonSerializer.Deserialize<MusicGenerationManifest>(
            File.ReadAllText(parsedApply.ManifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest is null || manifest.Plugins.Count == 0)
        {
            throw new InvalidDataException(
                $"生成MODのmanifestにプラグインがありません：{parsedApply.ManifestPath}");
        }

        var state = new MusicMo2ApplicationService().Apply(
            new MusicMo2ApplicationOptions
            {
                Mo2Root = parsedApply.Mo2Root,
                ProfileName = parsedApply.ProfileName,
                GeneratedModName = parsedApply.GeneratedModName,
                GeneratedPluginNames = manifest.Plugins
                    .Select(plugin => plugin.PluginFileName)
                    .ToArray(),
                EnableGeneratedMod = parsedApply.EnableGeneratedMod,
                SourcePluginNames = parsedApply.SourcePluginNames,
                DisableSourcePlugins = parsedApply.DisableSourcePlugins
            });
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                command = "mo2 apply",
                applied = true,
                profile = state.ProfilePath,
                state.GeneratedModEnabled,
                state.EnabledPlugins,
                state.DisabledPlugins,
                state.Changed
            },
            new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    if (!args[0].Equals("scan", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Unsupported command: {args[0]}");
        PrintHelp();
        return 2;
    }

    var parsed = ParseScanOptions(args[1..]);
    var options = parsed.Request;
    var progress = new Progress<ScanProgress>(value =>
    {
        var progressJson = JsonSerializer.Serialize(new
        {
            type = "progress",
            value.Level,
            value.Stage,
            value.Message,
            value.Current,
            value.Total,
            value.ModName,
            value.SourcePath,
            value.PluginName
        });
        Console.Error.WriteLine(progressJson);
    });

    var result = new MusicScanApplicationService().Scan(options, progress);
    MusicScanResultJson.Save(parsed.OutputPath, result);

    var summary = new
    {
        command = "scan",
        output = Path.GetFullPath(parsed.OutputPath),
        profile = result.Scan.Profile.ProfileName,
        mods = result.Scan.Mods.Count,
        plugins = result.Scan.Plugins.Count,
        musicRelevantRecords = result.Scan.Records.Count,
        assets = result.Scan.Assets.Count,
        musicSettings = result.MusicAnalysis.Settings.Count,
        duplicateGroups = result.AudioDuplicates.Groups.Count,
        warnings = result.Scan.WarningCount + result.MusicAnalysis.Issues.Count(issue => issue.Severity == ScanIssueSeverity.Warning)
    };
    Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Scan canceled");
    return 130;
}
catch (MusicGenerationApplicationException exception)
{
    Console.Error.WriteLine($"Generation plan error: {exception.Message}");
    return 20;
}
catch (MusicGenerationOutputException exception)
{
    Console.Error.WriteLine($"Generation error: {exception.Message}");
    return 30;
}
catch (DfgMusicGenerationOutputException exception)
{
    Console.Error.WriteLine($"DFG generation error: {exception.Message}");
    return 31;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
    return 1;
}

static (MusicScanRequest Request, string OutputPath) ParseScanOptions(IReadOnlyList<string> args)
{
    string? mo2Root = null;
    string? profile = null;
    var includeDisabled = false;
    var includeGenerated = false;
    var readPluginRecords = true;
    var scanArchives = true;
    var scanLooseAssets = true;
    var output = "scan-result.json";
    var excludedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    for (var index = 0; index < args.Count; index++)
    {
        var argument = args[index];
        switch (argument)
        {
            case "--mo2-root":
                mo2Root = ReadValue(args, ref index, argument);
                break;
            case "--profile":
                profile = ReadValue(args, ref index, argument);
                break;
            case "--output":
                output = ReadValue(args, ref index, argument);
                break;
            case "--exclude-mod":
                excludedMods.Add(ReadValue(args, ref index, argument));
                break;
            case "--include-disabled":
                includeDisabled = true;
                break;
            case "--include-generated-product":
                includeGenerated = true;
                break;
            case "--no-plugin-records":
                readPluginRecords = false;
                break;
            case "--no-archives":
                scanArchives = false;
                break;
            case "--no-loose-assets":
                scanLooseAssets = false;
                break;
            default:
                throw new ArgumentException($"Unknown option: {argument}");
        }
    }

    if (string.IsNullOrWhiteSpace(mo2Root))
    {
        throw new ArgumentException("--mo2-root is required");
    }

    var request = new MusicScanRequest
    {
        Mo2Root = Path.GetFullPath(mo2Root),
        ProfileName = profile,
        IncludeDisabledMods = includeDisabled,
        IncludeGeneratedProduct = includeGenerated,
        ReadPluginRecords = readPluginRecords,
        ScanArchives = scanArchives,
        ScanLooseAssets = scanLooseAssets,
        ExcludedModNames = excludedMods.ToArray()
    };
    return (request, output);
}

static (string ScanResultPath, string OutputPath, bool? KeepVanillaMusic)
    ParsePlanCreateOptions(IReadOnlyList<string> args)
{
    string? scanResultPath = null;
    var output = "generation-plan.json";
    bool? keepVanillaMusic = null;

    for (var index = 0; index < args.Count; index++)
    {
        var argument = args[index];
        switch (argument)
        {
            case "--scan-result":
                scanResultPath = ReadValue(args, ref index, argument);
                break;
            case "--output":
                output = ReadValue(args, ref index, argument);
                break;
            case "--keep-vanilla":
                if (keepVanillaMusic == false)
                {
                    throw new ArgumentException("--keep-vanilla and --remove-vanilla cannot be combined");
                }

                keepVanillaMusic = true;
                break;
            case "--remove-vanilla":
                if (keepVanillaMusic == true)
                {
                    throw new ArgumentException("--keep-vanilla and --remove-vanilla cannot be combined");
                }

                keepVanillaMusic = false;
                break;
            default:
                throw new ArgumentException($"Unknown option: {argument}");
        }
    }

    if (string.IsNullOrWhiteSpace(scanResultPath))
    {
        throw new ArgumentException("--scan-result is required");
    }

    return (Path.GetFullPath(scanResultPath), output, keepVanillaMusic);
}

static (string ScanResultPath, string PlanPath, MusicGenerationApplicationOptions Options,
    bool? KeepVanillaMusic) ParseGenerationOptions(IReadOnlyList<string> args)
{
    string? scanResultPath = null;
    string? planPath = null;
    string? outputModDirectory = null;
    var overwriteExisting = false;
    var worldSpace = false;
    bool? keepVanillaMusic = null;
    var existingMtdFileNames = new List<string>();

    for (var index = 0; index < args.Count; index++)
    {
        var argument = args[index];
        switch (argument)
        {
            case "--scan-result":
                scanResultPath = ReadValue(args, ref index, argument);
                break;
            case "--plan":
                planPath = ReadValue(args, ref index, argument);
                break;
            case "--output-mod":
                outputModDirectory = ReadValue(args, ref index, argument);
                break;
            case "--overwrite":
                overwriteExisting = true;
                break;
            case "--worldspace":
                worldSpace = true;
                break;
            case "--existing-mtd":
                existingMtdFileNames.Add(ReadValue(args, ref index, argument));
                break;
            case "--keep-vanilla":
                if (keepVanillaMusic == false)
                {
                    throw new ArgumentException("--keep-vanilla and --remove-vanilla cannot be combined");
                }

                keepVanillaMusic = true;
                break;
            case "--remove-vanilla":
                if (keepVanillaMusic == true)
                {
                    throw new ArgumentException("--keep-vanilla and --remove-vanilla cannot be combined");
                }

                keepVanillaMusic = false;
                break;
            default:
                throw new ArgumentException($"Unknown option: {argument}");
        }
    }

    if (string.IsNullOrWhiteSpace(scanResultPath))
    {
        throw new ArgumentException("--scan-result is required");
    }

    if (string.IsNullOrWhiteSpace(planPath))
    {
        throw new ArgumentException("--plan is required");
    }

    return (
        Path.GetFullPath(scanResultPath),
        Path.GetFullPath(planPath),
        new MusicGenerationApplicationOptions
        {
            OutputModDirectory = outputModDirectory,
            OverwriteExisting = overwriteExisting,
            WorldSpaceIndividualAssignment = worldSpace,
            ExistingMtdFileNames = existingMtdFileNames.Count == 0
                ? null
                : existingMtdFileNames
        },
        keepVanillaMusic);
}

static (string ScanResultPath, string PlanPath,
    DfgMusicGenerationApplicationOptions Options, bool? KeepVanillaMusic)
    ParseDfgGenerationOptions(IReadOnlyList<string> args)
{
    string? scanResultPath = null;
    string? planPath = null;
    string? outputModDirectory = null;
    string packageName = "GF Music Manager DFG";
    var overwriteExisting = false;
    var worldSpace = false;
    var existingMtdFileNames = new List<string>();
    bool? keepVanillaMusic = null;

    for (var index = 0; index < args.Count; index++)
    {
        switch (args[index])
        {
            case "--scan-result":
                scanResultPath = ReadValue(args, ref index, "--scan-result");
                break;
            case "--plan":
                planPath = ReadValue(args, ref index, "--plan");
                break;
            case "--output-mod":
                outputModDirectory = ReadValue(args, ref index, "--output-mod");
                break;
            case "--package-name":
                packageName = ReadValue(args, ref index, "--package-name");
                break;
            case "--overwrite":
                overwriteExisting = true;
                break;
            case "--worldspace":
                worldSpace = true;
                break;
            case "--existing-mtd":
                existingMtdFileNames.Add(ReadValue(args, ref index, "--existing-mtd"));
                break;
            case "--keep-vanilla":
                if (keepVanillaMusic == false)
                {
                    throw new ArgumentException(
                        "--keep-vanilla and --remove-vanilla cannot be combined");
                }

                keepVanillaMusic = true;
                break;
            case "--remove-vanilla":
                if (keepVanillaMusic == true)
                {
                    throw new ArgumentException(
                        "--keep-vanilla and --remove-vanilla cannot be combined");
                }

                keepVanillaMusic = false;
                break;
            default:
                throw new ArgumentException($"Unknown option: {args[index]}");
        }
    }

    if (string.IsNullOrWhiteSpace(scanResultPath))
    {
        throw new ArgumentException("--scan-result is required");
    }

    if (string.IsNullOrWhiteSpace(planPath))
    {
        throw new ArgumentException("--plan is required");
    }

    if (string.IsNullOrWhiteSpace(outputModDirectory))
    {
        throw new ArgumentException("--output-mod is required");
    }

    return (
        Path.GetFullPath(scanResultPath),
        Path.GetFullPath(planPath),
        new DfgMusicGenerationApplicationOptions
        {
                OutputModDirectory = Path.GetFullPath(outputModDirectory),
                PackageName = packageName,
                OverwriteExisting = overwriteExisting,
                WorldSpaceIndividualAssignment = worldSpace,
                ExistingMtdFileNames = existingMtdFileNames.Count == 0
                    ? null
                    : existingMtdFileNames
            },
        keepVanillaMusic);
}

static (string PlanPath, string OutputPath, IReadOnlyList<string> AdoptAssetKeys,
    IReadOnlyList<string> ExcludeAssetKeys, bool? KeepVanillaMusic)
    ParsePlanEditOptions(IReadOnlyList<string> args)
{
    string? planPath = null;
    string? outputPath = null;
    var adopt = new List<string>();
    var exclude = new List<string>();
    bool? keepVanillaMusic = null;

    for (var index = 0; index < args.Count; index++)
    {
        switch (args[index])
        {
            case "--plan":
                planPath = ReadValue(args, ref index, "--plan");
                break;
            case "--output":
                outputPath = ReadValue(args, ref index, "--output");
                break;
            case "--adopt":
                adopt.Add(ReadValue(args, ref index, "--adopt"));
                break;
            case "--exclude":
                exclude.Add(ReadValue(args, ref index, "--exclude"));
                break;
            case "--keep-vanilla":
                if (keepVanillaMusic == false)
                {
                    throw new ArgumentException("--keep-vanilla and --remove-vanilla cannot be combined");
                }

                keepVanillaMusic = true;
                break;
            case "--remove-vanilla":
                if (keepVanillaMusic == true)
                {
                    throw new ArgumentException("--keep-vanilla and --remove-vanilla cannot be combined");
                }

                keepVanillaMusic = false;
                break;
            default:
                throw new ArgumentException($"Unknown option: {args[index]}");
        }
    }

    if (string.IsNullOrWhiteSpace(planPath))
    {
        throw new ArgumentException("--plan is required");
    }

    if (string.IsNullOrWhiteSpace(outputPath))
    {
        throw new ArgumentException("--output is required");
    }

    return (
        Path.GetFullPath(planPath),
        Path.GetFullPath(outputPath),
        adopt,
        exclude,
        keepVanillaMusic);
}

static (string ScanResultPath, string OutputDirectory, string FileStem, int LongPlacementThreshold)
    ParseReportOptions(IReadOnlyList<string> args)
{
    string? scanResultPath = null;
    string? outputDirectory = null;
    var fileStem = "music-audit";
    var threshold = MusicAuditReportBuilder.DefaultLongPlacementThreshold;

    for (var index = 0; index < args.Count; index++)
    {
        switch (args[index])
        {
            case "--scan-result":
                scanResultPath = ReadValue(args, ref index, "--scan-result");
                break;
            case "--output-dir":
                outputDirectory = ReadValue(args, ref index, "--output-dir");
                break;
            case "--stem":
                fileStem = ReadValue(args, ref index, "--stem");
                break;
            case "--long-placement-threshold":
                if (!int.TryParse(ReadValue(args, ref index, "--long-placement-threshold"), out threshold) ||
                    threshold < 1)
                {
                    throw new ArgumentException("--long-placement-threshold must be a positive integer");
                }

                break;
            default:
                throw new ArgumentException($"Unknown option: {args[index]}");
        }
    }

    if (string.IsNullOrWhiteSpace(scanResultPath))
    {
        throw new ArgumentException("--scan-result is required");
    }

    if (string.IsNullOrWhiteSpace(outputDirectory))
    {
        throw new ArgumentException("--output-dir is required");
    }

    return (Path.GetFullPath(scanResultPath), Path.GetFullPath(outputDirectory), fileStem, threshold);
}

static (string? DraftPath, string? Mo2Root, string? ProfileName)
    ParseDraftResetOptions(IReadOnlyList<string> args)
{
    string? draftPath = null;
    string? mo2Root = null;
    string? profileName = null;
    for (var index = 0; index < args.Count; index++)
    {
        switch (args[index])
        {
            case "--draft":
                draftPath = ReadValue(args, ref index, "--draft");
                break;
            case "--mo2-root":
                mo2Root = ReadValue(args, ref index, "--mo2-root");
                break;
            case "--profile":
                profileName = ReadValue(args, ref index, "--profile");
                break;
            default:
                throw new ArgumentException($"Unknown option: {args[index]}");
        }
    }

    if (string.IsNullOrWhiteSpace(draftPath) &&
        (string.IsNullOrWhiteSpace(mo2Root) || string.IsNullOrWhiteSpace(profileName)))
    {
        throw new ArgumentException("--draft または --mo2-root と --profile が必要です");
    }

    return (
        string.IsNullOrWhiteSpace(draftPath) ? null : Path.GetFullPath(draftPath),
        mo2Root,
        profileName);
}

static (
    string Mo2Root,
    string ProfileName,
    string ScanResultPath,
    string PlanPath,
    string ManifestPath,
    MusicGenerationApplicationOptions GenerationOptions,
    string GeneratedModName,
    bool EnableGeneratedMod,
    IReadOnlyList<string> SourcePluginNames,
    bool DisableSourcePlugins,
    bool? KeepVanillaMusic)
    ParseMo2ApplyOptions(IReadOnlyList<string> args)
{
    string? mo2Root = null;
    string? profileName = null;
    string? scanResultPath = null;
    string? planPath = null;
    string? manifestPath = null;
    string? outputModDirectory = null;
    string generatedModName = "GF Music Product";
    var enableGeneratedMod = true;
    var disableGeneratedSpecified = false;
    var sourcePlugins = new List<string>();
    var disableSourcePlugins = false;
    bool? keepVanillaMusic = null;

    for (var index = 0; index < args.Count; index++)
    {
        switch (args[index])
        {
            case "--mo2-root": mo2Root = ReadValue(args, ref index, "--mo2-root"); break;
            case "--profile": profileName = ReadValue(args, ref index, "--profile"); break;
            case "--scan-result": scanResultPath = ReadValue(args, ref index, "--scan-result"); break;
            case "--plan": planPath = ReadValue(args, ref index, "--plan"); break;
            case "--manifest": manifestPath = ReadValue(args, ref index, "--manifest"); break;
            case "--output-mod": outputModDirectory = ReadValue(args, ref index, "--output-mod"); break;
            case "--generated-mod": generatedModName = ReadValue(args, ref index, "--generated-mod"); break;
            case "--disable-generated":
                if (disableGeneratedSpecified) throw new ArgumentException("--enable-generated and --disable-generated cannot be combined");
                enableGeneratedMod = false;
                disableGeneratedSpecified = true;
                break;
            case "--enable-generated":
                if (disableGeneratedSpecified) throw new ArgumentException("--enable-generated and --disable-generated cannot be combined");
                enableGeneratedMod = true;
                disableGeneratedSpecified = true;
                break;
            case "--source-plugin": sourcePlugins.Add(ReadValue(args, ref index, "--source-plugin")); break;
            case "--disable-source-plugins": disableSourcePlugins = true; break;
            case "--keep-vanilla":
                if (keepVanillaMusic == false) throw new ArgumentException("--keep-vanilla and --remove-vanilla cannot be combined");
                keepVanillaMusic = true;
                break;
            case "--remove-vanilla":
                if (keepVanillaMusic == true) throw new ArgumentException("--keep-vanilla and --remove-vanilla cannot be combined");
                keepVanillaMusic = false;
                break;
            default: throw new ArgumentException($"Unknown option: {args[index]}");
        }
    }

    if (string.IsNullOrWhiteSpace(mo2Root) || string.IsNullOrWhiteSpace(profileName) ||
        string.IsNullOrWhiteSpace(scanResultPath) || string.IsNullOrWhiteSpace(planPath) ||
        string.IsNullOrWhiteSpace(manifestPath))
    {
        throw new ArgumentException(
            "--mo2-root、--profile、--scan-result、--plan、--manifest は必須です");
    }

    return (
        Path.GetFullPath(mo2Root),
        profileName,
        Path.GetFullPath(scanResultPath),
        Path.GetFullPath(planPath),
        Path.GetFullPath(manifestPath),
        new MusicGenerationApplicationOptions
        {
            OutputModDirectory = outputModDirectory,
            WorldSpaceIndividualAssignment = false,
            OverwriteExisting = true
        },
        generatedModName,
        enableGeneratedMod,
        sourcePlugins,
        disableSourcePlugins,
        keepVanillaMusic);
}

static MusicPlanSnapshot ApplyVanillaOverride(
    MusicPlanSnapshot snapshot,
    bool? keepVanillaMusic) =>
    keepVanillaMusic.HasValue
        ? snapshot with { KeepVanillaMusic = keepVanillaMusic }
        : snapshot;

static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
{
    if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
    {
        throw new ArgumentException($"{option} requires a value");
    }

    index++;
    return args[index];
}

static void PrintHelp()
{
    Console.WriteLine("GF Music Manager CLI");
    Console.WriteLine("  scan --mo2-root <path> [--profile <name>] [--include-disabled]");
    Console.WriteLine("       [--output <path>] [--include-generated-product]");
    Console.WriteLine("       [--exclude-mod <name>] [--no-plugin-records] [--no-archives]");
    Console.WriteLine("       [--no-loose-assets]");
    Console.WriteLine("  plan create --scan-result <path> [--output <path>]");
    Console.WriteLine("       [--keep-vanilla | --remove-vanilla]");
    Console.WriteLine("  plan edit --plan <path> --output <path> [--adopt <asset-key> ...]");
    Console.WriteLine("       [--exclude <asset-key> ...] [--keep-vanilla | --remove-vanilla]");
    Console.WriteLine("  plan validate --scan-result <path> --plan <path> [--output-mod <path>]");
    Console.WriteLine("       [--overwrite] [--worldspace] [--keep-vanilla | --remove-vanilla]");
    Console.WriteLine("  report --scan-result <path> --output-dir <path> [--stem <name>]");
    Console.WriteLine("       [--long-placement-threshold <number>]");
    Console.WriteLine("  generate --scan-result <path> --plan <path> [--output-mod <path>]");
    Console.WriteLine("       [--overwrite] [--worldspace] [--existing-mtd <name> ...]");
    Console.WriteLine("       [--keep-vanilla | --remove-vanilla]");
    Console.WriteLine("  dfg generate --scan-result <path> --plan <path> --output-mod <path>");
    Console.WriteLine("       [--package-name <name>] [--overwrite] [--worldspace]");
    Console.WriteLine("       [--existing-mtd <name> ...] [--keep-vanilla | --remove-vanilla]");
    Console.WriteLine("       [--keep-vanilla | --remove-vanilla]");
    Console.WriteLine("  validate --scan-result <path> --plan <path> [--output-mod <path>]");
    Console.WriteLine("       [--keep-vanilla | --remove-vanilla]");
    Console.WriteLine("  draft reset --draft <path> | --mo2-root <path> --profile <name>");
    Console.WriteLine("  mo2 apply --mo2-root <path> --profile <name> --scan-result <path>");
    Console.WriteLine("       --plan <path> --manifest <path> [--output-mod <path>]");
    Console.WriteLine("       [--disable-generated] [--source-plugin <name> ...]");
    Console.WriteLine("       [--disable-source-plugins] [--keep-vanilla | --remove-vanilla]");
}
