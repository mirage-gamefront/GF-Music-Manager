using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Archives;
using System.Diagnostics;
using System.Text.Json;

namespace GfMusicManager.Application;

/// <summary>
/// Shared generation use case for WPF and CLI.
///
/// It restores a saved plan against the current scan result, validates the
/// user-owned decisions, and then delegates materialization and post-output
/// diagnostics to the Core writer.  It never changes MO2 profile state.
/// </summary>
public sealed class MusicGenerationApplicationService
{
    private readonly MusicPlanApplicationService _planService;
    private readonly MusicGenerationOutputWriter _outputWriter;
    private readonly MusicTypeDistributorOutputNameResolver _mtdNameResolver;
    private readonly MusicGenerationPlanResolver _planResolver;
    private readonly MusicGenerationPostGenerationDiagnostic _postGenerationDiagnostic;

    public MusicGenerationApplicationService(
        MusicPlanApplicationService? planService = null,
        MusicGenerationOutputWriter? outputWriter = null,
        MusicTypeDistributorOutputNameResolver? mtdNameResolver = null,
        MusicGenerationPlanResolver? planResolver = null)
    {
        _planService = planService ?? new MusicPlanApplicationService();
        _outputWriter = outputWriter ?? new MusicGenerationOutputWriter();
        _mtdNameResolver = mtdNameResolver ?? new MusicTypeDistributorOutputNameResolver();
        _planResolver = planResolver ?? new MusicGenerationPlanResolver();
        _postGenerationDiagnostic = new MusicGenerationPostGenerationDiagnostic(
            new BsaArchiveReader());
    }

    public MusicGenerationApplicationValidationResult Validate(
        MusicScanApplicationResult scanResult,
        MusicPlanSnapshot planSnapshot,
        MusicGenerationApplicationOptions options)
        => Validate(scanResult, planSnapshot, options, preparedGeneration: null);

    private MusicGenerationApplicationValidationResult Validate(
        MusicScanApplicationResult scanResult,
        MusicPlanSnapshot planSnapshot,
        MusicGenerationApplicationOptions options,
        PreparedGeneration? preparedGeneration)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        ArgumentNullException.ThrowIfNull(planSnapshot);
        ArgumentNullException.ThrowIfNull(options);

        var outputDirectory = ResolveOutputDirectory(scanResult, options);
        var errors = new List<string>();
        var warnings = new List<string>();
        var entryCount = 0;
        var adoptedEntryCount = 0;
        var trackCount = 0;
        var conflictCount = 0;
        try
        {
            var prepared = preparedGeneration ?? Prepare(scanResult, planSnapshot);
            var conflicts = prepared.Plan.Conflicts;
            entryCount = prepared.Plan.Entries.Count;
            var adoptedEntries = prepared.Plan.Entries
                .Where(entry => entry.IsAdopted)
                .ToArray();
            adoptedEntryCount = adoptedEntries.Length;
            trackCount = adoptedEntries.Sum(entry => entry.Tracks.Count);
            conflictCount = conflicts.Count;
            var resolution = prepared.Plan.KeepVanillaMusic is null
                ? null
                : _planResolver.Resolve(
                    prepared.Plan,
                    scanResult.MusicAnalysis.Settings);
            return BuildValidation(
                scanResult,
                options,
                outputDirectory,
                prepared,
                conflicts,
                resolution);
        }
        catch (MusicGenerationApplicationException exception)
        {
            errors.Add(exception.Message);
        }
        catch (MusicGenerationOutputException exception)
        {
            errors.Add(exception.Message);
        }
        catch (Exception exception)
        {
            errors.Add(UiText.Format(
                "Generation.Preflight.Exception",
                GenerationMessageFormatter.ExceptionDetail(exception)));
        }

        return new MusicGenerationApplicationValidationResult(
            errors.Count == 0,
            outputDirectory,
            entryCount,
            adoptedEntryCount,
            trackCount,
            0,
            conflictCount,
            errors,
            warnings);
    }

    public MusicGenerationApplicationResult Generate(
        MusicScanApplicationResult scanResult,
        MusicPlanSnapshot planSnapshot,
        MusicGenerationApplicationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        ArgumentNullException.ThrowIfNull(planSnapshot);
        ArgumentNullException.ThrowIfNull(options);

        return GeneratePrepared(
            scanResult,
            options,
            () => Prepare(scanResult, planSnapshot, options.Progress),
            cancellationToken);
    }

    /// <summary>
    /// Generates from the editable plan that was already built for the current
    /// scan.  The desktop caller owns that plan, so rebuilding the same plan
    /// and restoring its snapshot would only repeat the expensive preparation
    /// work immediately before output.
    /// </summary>
    public MusicGenerationApplicationResult Generate(
        MusicScanApplicationResult scanResult,
        MusicGenerationPlan preparedPlan,
        MusicGenerationApplicationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        ArgumentNullException.ThrowIfNull(preparedPlan);
        ArgumentNullException.ThrowIfNull(options);

        return GeneratePrepared(
            scanResult,
            options,
            () => PrepareExistingPlan(preparedPlan),
            cancellationToken);
    }

    private MusicGenerationApplicationResult GeneratePrepared(
        MusicScanApplicationResult scanResult,
        MusicGenerationApplicationOptions options,
        Func<PreparedGeneration> prepare,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepare);

        var generationStopwatch = Stopwatch.StartNew();
        ReportProgress(
            options.Progress,
            MusicGenerationProgressStage.Preparing,
            UiText.Get("Progress.GenerationPlanPreparing"),
            5);
        var outputDirectory = ResolveOutputDirectory(scanResult, options);
        var prepared = prepare();
        GfMusicManagerLog.Info(
            $"Generation application: plan prepared in {generationStopwatch.Elapsed}. " +
            $"entries={prepared.Plan.Entries.Count}, restored={prepared.PlanApplication.RestoredEntryCount}.");
        ReportProgress(
            options.Progress,
            MusicGenerationProgressStage.Resolving,
            UiText.Get("Progress.GenerationResolving"),
            15,
            0,
            1);
        var conflicts = prepared.Plan.Conflicts;
        GfMusicManagerLog.Info(
            $"Generation application: conflict snapshot loaded in {generationStopwatch.Elapsed}. " +
            $"conflicts={conflicts.Count}.");
        var resolution = prepared.Plan.KeepVanillaMusic is null
            ? null
            : _planResolver.Resolve(
                prepared.Plan,
                scanResult.MusicAnalysis.Settings);
        GfMusicManagerLog.Info(
            $"Generation application: Music Type resolution completed in {generationStopwatch.Elapsed}. " +
            $"musicTypes={resolution?.MusicTypes.Count ?? 0}, " +
            $"integrationTargets={resolution?.IntegrationTargets.Count ?? 0}.");
        ReportProgress(
            options.Progress,
            MusicGenerationProgressStage.Validating,
            UiText.Get("Progress.GenerationValidating"),
            30,
            1,
            1);
        var validation = BuildValidation(
            scanResult,
            options,
            outputDirectory,
            prepared,
            conflicts,
            resolution);
        GfMusicManagerLog.Info(
            $"Generation application: preflight completed in {generationStopwatch.Elapsed}. " +
            $"valid={validation.IsValid}, errors={validation.Errors.Count}, warnings={validation.Warnings.Count}.");
        if (!validation.IsValid)
        {
            throw new MusicGenerationApplicationException(
                UiText.Format(
                    "Generation.Preflight.Failed",
                    string.Join(Environment.NewLine, validation.Errors)));
        }

        var output = _outputWriter.Generate(
            prepared.Plan,
            scanResult.MusicAnalysis.Settings,
            CreateCoreOptions(scanResult, options, validation.OutputModDirectory),
            cancellationToken,
            resolution!,
            conflicts);
        GfMusicManagerLog.Info(
            $"Generation application: output completed in {generationStopwatch.Elapsed}.");

        return new MusicGenerationApplicationResult(
            prepared.Plan,
            prepared.PlanApplication,
            output);
    }

    public MusicGenerationOutputValidationResult ValidateOutput(
        MusicScanApplicationResult scanResult,
        MusicPlanSnapshot planSnapshot,
        MusicGenerationApplicationOptions options)
    {
        ArgumentNullException.ThrowIfNull(scanResult);
        ArgumentNullException.ThrowIfNull(planSnapshot);
        ArgumentNullException.ThrowIfNull(options);

        var outputDirectory = ResolveOutputDirectory(scanResult, options);
        var manifestPath = Path.Combine(outputDirectory, ExistingMusicProductLoader.ManifestFileName);
        try
        {
            if (!File.Exists(manifestPath))
            {
                return CreateOutputValidationFailure(
                    outputDirectory,
                    manifestPath,
                    UiText.Format("Generation.OutputValidation.ManifestMissing", manifestPath));
            }

            var manifest = JsonSerializer.Deserialize<MusicGenerationManifest>(
                File.ReadAllText(manifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest is null)
            {
                return CreateOutputValidationFailure(
                    outputDirectory,
                    manifestPath,
                    UiText.Get("Generation.OutputValidation.ManifestUnreadable"));
            }

            if (manifest.SchemaVersion is < 5 or > 5)
            {
                return CreateOutputValidationFailure(
                    outputDirectory,
                    manifestPath,
                    UiText.Format(
                        "Generation.OutputValidation.ManifestSchemaUnsupported",
                        manifest.SchemaVersion));
            }

            var prepared = Prepare(scanResult, planSnapshot);
            var planResolution = _planResolver.Resolve(
                prepared.Plan,
                scanResult.MusicAnalysis.Settings);
            var mtdPath = Path.Combine(
                outputDirectory,
                Path.GetFileName(manifest.MtdFileName));
            var tracks = manifest.Tracks ?? Array.Empty<GeneratedMusicTrackOutput>();
            var plugins = manifest.Plugins ?? Array.Empty<GeneratedPluginOutput>();
            var worldSpaces = manifest.WorldSpaces ?? Array.Empty<GeneratedWorldSpaceOutput>();
            var cells = manifest.Cells ?? Array.Empty<GeneratedCellOutput>();
            var integratedMusicTypes = manifest.IntegratedMusicTypes ??
                Array.Empty<GeneratedMusicTypeOutput>();
            var assets = manifest.Assets ?? Array.Empty<GeneratedAssetOutput>();
            var diagnostic = _postGenerationDiagnostic.Run(
                outputDirectory,
                outputDirectory,
                mtdPath,
                manifestPath,
                scanResult.MusicAnalysis.Settings,
                plugins,
                tracks,
                worldSpaces,
                cells,
                integratedMusicTypes,
                manifest.CellSkyPatcherFileName,
                assets,
                manifest.WorldSpaceIndividualAssignment,
                manifest.NewRecordCount,
                manifest.MaxNewRecordsPerPlugin,
                planResolution,
                default);

            return new MusicGenerationOutputValidationResult(
                diagnostic.IsSuccess,
                outputDirectory,
                manifestPath,
                diagnostic);
        }
        catch (MusicGenerationApplicationException exception)
        {
            return CreateOutputValidationFailure(
                outputDirectory,
                manifestPath,
                exception.Message);
        }
        catch (Exception exception)
        {
            return CreateOutputValidationFailure(
                outputDirectory,
                manifestPath,
                UiText.Format(
                    "Generation.OutputValidation.Failed",
                    GenerationMessageFormatter.ExceptionDetail(exception)));
        }
    }

    private PreparedGeneration Prepare(
        MusicScanApplicationResult scanResult,
        MusicPlanSnapshot planSnapshot,
        IProgress<MusicGenerationProgress>? progress = null)
    {
        var prepareStopwatch = Stopwatch.StartNew();
        var planStopwatch = Stopwatch.StartNew();
        var plan = _planService.CreatePlan(scanResult, progress);
        GfMusicManagerLog.Info(
            $"Generation application: plan creation completed in {planStopwatch.Elapsed}. " +
            $"entries={plan.Entries.Count}.");

        var applyStopwatch = Stopwatch.StartNew();
        var application = _planService.Apply(
            plan,
            planSnapshot,
            scanResult.MusicAnalysis.Settings);
        GfMusicManagerLog.Info(
            $"Generation application: plan snapshot restore completed in {applyStopwatch.Elapsed}. " +
            $"restored={application.RestoredEntryCount}, missing={application.MissingEntryCount}.");

        if (application.MissingEntryCount > 0 ||
            application.RestoredEntryCount != plan.Entries.Count)
        {
            var missing = application.MissingEntryCount > 0
                ? UiText.Format(
                    "Generation.PlanRestore.MismatchedKeys",
                    string.Join(", ", application.MissingAssetKeys.Take(5)))
                : string.Empty;
            throw new MusicGenerationApplicationException(
                UiText.Format(
                    "Generation.PlanRestore.Mismatch",
                    application.RestoredEntryCount,
                    plan.Entries.Count,
                    missing));
        }

        GfMusicManagerLog.Info(
            $"Generation application: prepare completed in {prepareStopwatch.Elapsed}.");

        return new PreparedGeneration(plan, application);
    }

    private static PreparedGeneration PrepareExistingPlan(
        MusicGenerationPlan plan)
    {
        var restored = new MusicPlanApplyResult(
            plan.Entries.Count,
            0,
            Array.Empty<string>());
        return new PreparedGeneration(plan, restored);
    }

    private MusicGenerationOutputOptions CreateCoreOptions(
        MusicScanApplicationResult scanResult,
        MusicGenerationApplicationOptions options,
        string outputDirectory) =>
        new()
        {
            OutputModDirectory = outputDirectory,
            CapacityPolicy = options.CapacityPolicy,
            OverwriteExisting = options.OverwriteExisting,
            WorldSpaceIndividualAssignment = options.WorldSpaceIndividualAssignment,
            SelectedWorldSpaceFormKeys = options.SelectedWorldSpaceFormKeys,
            ExistingMtdFileNames = ResolveExistingMtdFileNames(scanResult, options),
            Progress = options.Progress
        };

    private MusicGenerationApplicationValidationResult BuildValidation(
        MusicScanApplicationResult scanResult,
        MusicGenerationApplicationOptions options,
        string outputDirectory,
        PreparedGeneration prepared,
        IReadOnlyList<MusicGenerationPlanConflict> conflicts,
        MusicGenerationPlanResolution? resolution)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var plan = prepared.Plan;
        var adoptedEntries = plan.Entries
            .Where(entry => entry.IsAdopted)
            .ToArray();
        var integrationConflictCount = conflicts.Count(conflict =>
            conflict.Kind == MusicGenerationPlanConflictKind.MultipleGeneratedMusicTypesForRecord);

        if (plan.KeepVanillaMusic is null)
        {
            errors.Add(UiText.Get("Generation.Preflight.KeepVanillaRequired"));
        }

        if (adoptedEntries.Length == 0)
        {
            errors.Add(UiText.Get("Generation.Preflight.NoAdoptedAssets"));
        }

        var missingAssets = adoptedEntries.Count(entry => entry.Asset is null);
        if (missingAssets > 0)
        {
            errors.Add(UiText.Format("Generation.Preflight.MissingAssets", missingAssets));
        }

        var emptyTrackEntries = adoptedEntries.Count(entry => entry.Tracks.Count == 0);
        if (emptyTrackEntries > 0)
        {
            errors.Add(UiText.Format("Generation.Preflight.EmptyTracks", emptyTrackEntries));
        }

        if (integrationConflictCount > 0)
        {
            warnings.Add(
                UiText.Format(
                    "Generation.Preflight.IntegrationWarning",
                    integrationConflictCount));
        }

        var existingMtdFileNames = ResolveExistingMtdFileNames(scanResult, options);
        _mtdNameResolver.SelectOutputFileName(existingMtdFileNames);

        if (Directory.Exists(outputDirectory))
        {
            if (options.OverwriteExisting)
            {
                warnings.Add(UiText.Get("Generation.Preflight.ExistingProductReplace"));
            }
            else
            {
                errors.Add(
                    UiText.Format("Generation.Preflight.OutputExists", outputDirectory));
            }
        }

        return new MusicGenerationApplicationValidationResult(
            errors.Count == 0,
            outputDirectory,
            plan.Entries.Count,
            adoptedEntries.Length,
            adoptedEntries.Sum(entry => entry.Tracks.Count),
            resolution?.IntegrationTargets.Count ?? 0,
            conflicts.Count,
            errors,
            warnings);
    }

    private static void ReportProgress(
        IProgress<MusicGenerationProgress>? progress,
        MusicGenerationProgressStage stage,
        string message,
        double percent,
        int current = 0,
        int total = 0) =>
        progress?.Report(new MusicGenerationProgress(
            stage,
            message,
            Math.Clamp(percent, 0, 100),
            current,
            total));

    private static string ResolveOutputDirectory(
        MusicScanApplicationResult scanResult,
        MusicGenerationApplicationOptions options)
    {
        var outputDirectory = string.IsNullOrWhiteSpace(options.OutputModDirectory)
            ? Path.Combine(scanResult.Request.Mo2Root, "mods", "GF Music Product")
            : options.OutputModDirectory!;
        return Path.GetFullPath(outputDirectory);
    }

    private static MusicGenerationOutputValidationResult CreateOutputValidationFailure(
        string outputDirectory,
        string manifestPath,
        string error)
    {
        var diagnostic = new MusicGenerationDiagnosticResult(
            false,
            Array.Empty<string>(),
            new[] { error });
        return new MusicGenerationOutputValidationResult(
            false,
            outputDirectory,
            manifestPath,
            diagnostic);
    }

    private static IReadOnlyList<string> ResolveExistingMtdFileNames(
        MusicScanApplicationResult scanResult,
        MusicGenerationApplicationOptions options) =>
        options.ExistingMtdFileNames ??
        MusicTypeDistributorOutputNameResolver.DiscoverExistingFileNames(
            scanResult.Scan.Mods);

    private sealed record PreparedGeneration(
        MusicGenerationPlan Plan,
        MusicPlanApplyResult PlanApplication);
}
