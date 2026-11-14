using System.Text;
using System.Text.Json;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using SkyrimScan.Core.Archives;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// Options for the first real generation stage.  The writer is deliberately
/// independent from MO2: the caller supplies the exact output mod directory.
/// </summary>
public sealed record MusicGenerationOutputOptions
{
    public required string OutputModDirectory { get; init; }

    public MusicGenerationCapacityPolicy CapacityPolicy { get; init; } =
        MusicGenerationCapacityPolicy.CurrentAe;

    public bool OverwriteExisting { get; init; }

    public bool WorldSpaceIndividualAssignment { get; init; }

    public IReadOnlySet<string> SelectedWorldSpaceFormKeys { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ExistingMtdFileNames { get; init; } =
        Array.Empty<string>();

    /// <summary>
    /// Optional progress sink.  Generation remains usable by CLI and tests
    /// when no UI progress sink is supplied.
    /// </summary>
    public IProgress<MusicGenerationProgress>? Progress { get; init; }
}

public sealed record MusicGenerationOutputResult(
    string OutputModDirectory,
    string MtdFilePath,
    string ManifestPath,
    IReadOnlyList<GeneratedPluginOutput> Plugins,
    IReadOnlyList<GeneratedMusicTrackOutput> Tracks,
    IReadOnlyList<GeneratedWorldSpaceOutput> WorldSpaces,
    IReadOnlyList<GeneratedCellOutput> Cells,
    string? CellSkyPatcherFilePath,
    IReadOnlyList<GeneratedAssetOutput> Assets,
    IReadOnlyList<string> Warnings)
{
    public required MusicGenerationDiagnosticResult Diagnostic { get; init; }

    public IReadOnlyList<GeneratedMusicTypeOutput> IntegratedMusicTypes { get; init; } =
        Array.Empty<GeneratedMusicTypeOutput>();
}

public sealed record GeneratedPluginOutput(
    string PluginFileName,
    int NewMusicTrackRecordCount,
    int NewMusicTypeRecordCount,
    int WorldSpaceOverrideRecordCount)
{
    public int NewSupportRecordCount { get; init; }
}

public sealed record GeneratedMusicTrackOutput(
    string AssetKey,
    string VirtualPath,
    string EditorId,
    string FormKey,
    string PluginFileName,
    IReadOnlyList<string> DestinationKeys,
    IReadOnlyList<MusicConditionSource> Conditions)
{
    public string TrackKey { get; init; } = string.Empty;

    public string ModName { get; init; } = string.Empty;

    public string? FinaleVirtualPath { get; init; }
}

public sealed record GeneratedWorldSpaceOutput(
    string WorldSpaceFormKey,
    string? WorldSpaceEditorId,
    string MusicTypeEditorId,
    string MusicTypeFormKey,
    string PluginFileName,
    IReadOnlyList<string> TrackFormKeys);

public sealed record GeneratedMusicTypeOutput(
    string TargetKey,
    MusicSettingScope Scope,
    string ScopeFormKey,
    string? ScopeEditorId,
    string MusicTypeEditorId,
    string MusicTypeFormKey,
    string PluginFileName,
    IReadOnlyList<string> TrackFormKeys);

public sealed record GeneratedAssetOutput(
    string AssetKey,
    string VirtualPath,
    AssetSourceKind SourceKind,
    string SourcePath,
    string? ArchiveEntryPath,
    string? OutputPath,
    long Length,
    bool IsCopied)
{
    public string ModName { get; init; } = string.Empty;

    public IReadOnlyList<string> TrackKeys { get; init; } = Array.Empty<string>();
}

public sealed class MusicGenerationOutputException : InvalidOperationException
{
    public MusicGenerationOutputException(string message)
        : base(message)
    {
    }

    public MusicGenerationOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Materializes adopted music into a real generated mod directory.
///
/// The normal path produces new MusicTrack records plus a valid MTD file that
/// points existing Music Types, Locations and Regions at those tracks.  An
/// assignment conflict creates one integrated MusicType per target in the
/// generated ESP.  The optional WorldSpace path additionally creates stable
/// MusicType records and WorldSpace overrides in the generated ESP.
/// </summary>
public sealed class MusicGenerationOutputWriter
{
    private const string GeneratedModName = "GF Music Product";
    private const string ManifestFileName = "GFMusicProduct.json";

    private readonly BsaArchiveReader _archiveReader;
    private readonly MusicGenerationCapacityPlanner _capacityPlanner;
    private readonly MusicGenerationPlanResolver _planResolver;
    private readonly MusicTypeDistributorOutputNameResolver _mtdNameResolver;
    private readonly MusicGenerationPostGenerationDiagnostic _postGenerationDiagnostic;

    public MusicGenerationOutputWriter(
        BsaArchiveReader? archiveReader = null,
        MusicGenerationCapacityPlanner? capacityPlanner = null,
        MusicGenerationPlanResolver? planResolver = null,
        MusicTypeDistributorOutputNameResolver? mtdNameResolver = null)
    {
        _archiveReader = archiveReader ?? new BsaArchiveReader();
        _capacityPlanner = capacityPlanner ?? new MusicGenerationCapacityPlanner();
        _planResolver = planResolver ?? new MusicGenerationPlanResolver();
        _mtdNameResolver = mtdNameResolver ?? new MusicTypeDistributorOutputNameResolver();
        _postGenerationDiagnostic = new MusicGenerationPostGenerationDiagnostic(_archiveReader);
    }

    public MusicGenerationOutputResult Generate(
        MusicGenerationPlan plan,
        IReadOnlyList<MusicSettingSource> settings,
        MusicGenerationOutputOptions options,
        CancellationToken cancellationToken = default,
        MusicGenerationPlanResolution? suppliedPlanResolution = null,
        IReadOnlyList<MusicGenerationPlanConflict>? suppliedConflicts = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.OutputModDirectory))
        {
            throw new ArgumentException(
                "The generated mod output directory is required.",
                nameof(options));
        }

        var outputDirectory = Path.GetFullPath(options.OutputModDirectory);
        ReportProgress(
            options.Progress,
            MusicGenerationProgressStage.Writing,
            UiText.Get("Progress.GenerationCapacity"),
            35);
        var mtdFileName = _mtdNameResolver.SelectOutputFileName(
            options.ExistingMtdFileNames);
        if (Directory.Exists(outputDirectory) && !options.OverwriteExisting)
        {
            throw new MusicGenerationOutputException(
                UiText.Format("Generation.Output.OutputExists", outputDirectory));
        }

        if (plan.KeepVanillaMusic is null)
        {
            throw new MusicGenerationOutputException(
                UiText.Get("Generation.Preflight.KeepVanillaRequired"));
        }

        var adoptedEntries = plan.Entries
            .Where(entry => entry.IsAdopted)
            .ToArray();
        if (adoptedEntries.Length == 0)
        {
            throw new MusicGenerationOutputException(
                UiText.Get("Generation.Preflight.NoAdoptedAssets"));
        }

        var entriesWithMissingAssets = adoptedEntries
            .Where(entry => entry.Asset is null)
            .ToArray();
        if (entriesWithMissingAssets.Length > 0)
        {
            throw new MusicGenerationOutputException(
                UiText.Format(
                    "Generation.Preflight.MissingAssets",
                    entriesWithMissingAssets.Length));
        }

        var assetPathMap = MusicGenerationAssetPathMap.Create(adoptedEntries);
        ValidateConditions(adoptedEntries);
        var conditionDependencies = MusicConditionDependencyMaterializer.Create(
            adoptedEntries,
            settings);
        var planResolution = suppliedPlanResolution ?? _planResolver.Resolve(plan, settings);
        var settingsByKey = BuildSettingsIndex(settings);
        var worldSpaceTargets = BuildWorldSpaceTargets(
            adoptedEntries,
            settingsByKey,
            options);
        ValidateWorldSpaceConflicts(
            planResolution,
            worldSpaceTargets,
            options.WorldSpaceIndividualAssignment);
        var integrationTargets = planResolution.IntegrationTargets
            .Where(target => target.Scope != MusicSettingScope.WorldSpace)
            .ToArray();
        var staticTrackAssetKeys = adoptedEntries
            .Select(entry => entry.AssetKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var capacity = _capacityPlanner.Estimate(
            plan,
            options.CapacityPolicy,
            options.WorldSpaceIndividualAssignment,
            worldSpaceTargets.Select(target => target.FormKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            integrationTargets.Length,
            staticTrackAssetKeys,
            conditionDependencies.DependencyCount);
        if (!capacity.IsValid)
        {
            throw new MusicGenerationOutputException(
                UiText.Get("Generation.Output.CapacityInvalid"));
        }

        var stageDirectory = outputDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            $".generating-{Guid.NewGuid():N}";
        var committed = false;
        try
        {
            ReportProgress(
                options.Progress,
                MusicGenerationProgressStage.Writing,
                UiText.Get("Progress.GenerationWriting"),
                40);
            GfMusicManagerLog.Info(
                $"Generation: begin. output={outputDirectory}, adopted={adoptedEntries.Length}, " +
                $"worldSpaceSettings={options.WorldSpaceIndividualAssignment}, " +
                $"worldSpaces={worldSpaceTargets.Count}, integrationTargets={integrationTargets.Length}, " +
                $"plugins={capacity.Plugins.Count}.");
            Directory.CreateDirectory(stageDirectory);

            var pluginContexts = CreatePluginContexts(capacity.Plugins);
            var pluginContextByFileName = pluginContexts.ToDictionary(
                context => context.PluginFileName,
                StringComparer.OrdinalIgnoreCase);
            var primaryPlugin = pluginContexts.FirstOrDefault() ??
                                throw new MusicGenerationOutputException(
                                    UiText.Get("Generation.Output.PrimaryPluginMissing"));
            conditionDependencies.MaterializeInto(primaryPlugin.Mod);
            var trackOutputs = new List<GeneratedMusicTrackOutput>(
                adoptedEntries.Sum(entry => Math.Max(entry.Tracks.Count, 1)));
            var assetOutputs = new List<GeneratedAssetOutput>(adoptedEntries.Length);
            var outputAssetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var trackKeysByAssetKey = BuildTrackKeysByAssetKey(adoptedEntries);
            void AddAssetOutput(AssetSource source)
            {
                var sourceKey = MusicGenerationPlanEntry.CreateAssetKey(source);
                if (outputAssetKeys.Add(sourceKey))
                {
                    var trackKeys = trackKeysByAssetKey.TryGetValue(
                        sourceKey,
                        out var relatedTrackKeys)
                        ? relatedTrackKeys
                        : Array.Empty<string>();
                    assetOutputs.Add(CopyAsset(
                        source,
                        assetPathMap,
                        stageDirectory,
                        cancellationToken,
                        trackKeys));
                }
            }
            var generatedTracks = new Dictionary<string, IReadOnlyList<IMusicTrack>>(
                StringComparer.OrdinalIgnoreCase);
            var entryByAssetKey = adoptedEntries.ToDictionary(
                entry => entry.AssetKey,
                StringComparer.OrdinalIgnoreCase);
            var processedAssetKeys = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var processedEntryCount = 0;
            var totalEntryCount = adoptedEntries.Length;
            void ReportEntryProgress()
            {
                processedEntryCount++;
                if (processedEntryCount == 1 ||
                    processedEntryCount % 25 == 0 ||
                    processedEntryCount == totalEntryCount)
                {
                    ReportProgress(
                        options.Progress,
                        MusicGenerationProgressStage.Writing,
                        UiText.Get("Progress.GenerationWriting"),
                        40 + processedEntryCount * 28d / totalEntryCount,
                        processedEntryCount,
                        totalEntryCount);
                }
            }
            foreach (var pluginEstimate in capacity.Plugins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var context = pluginContextByFileName[pluginEstimate.PluginFileName];
                foreach (var assetKey in pluginEstimate.AssetKeys)
                {
                    var entry = entryByAssetKey[assetKey];
                    var asset = entry.Asset!;
                    processedAssetKeys.Add(assetKey);
                    var generatedForAsset = new List<IMusicTrack>(entry.Tracks.Count);
                    foreach (var trackPlan in entry.Tracks)
                    {
                        var generatedConditions = conditionDependencies.RemapConditions(
                            trackPlan.Conditions);
                        var track = AddMusicTrack(
                            context.Mod,
                            asset,
                            trackPlan,
                            generatedConditions,
                            assetPathMap,
                            context.UsedMusicTrackEditorIds);
                        generatedForAsset.Add(track);
                        trackOutputs.Add(new GeneratedMusicTrackOutput(
                            assetKey,
                            assetPathMap.GetOutputPath(asset),
                            track.EditorID ?? string.Empty,
                            track.FormKey.ToString(),
                            pluginEstimate.PluginFileName,
                            entry.DestinationKeys
                                .Select(CreateDestinationKey)
                                .ToArray(),
                            generatedConditions)
                        {
                            TrackKey = trackPlan.TrackKey,
                            ModName = asset.ModName,
                            FinaleVirtualPath = trackPlan.FinaleAsset is null
                                ? trackPlan.FinaleAudioPath
                                : assetPathMap.GetOutputPath(trackPlan.FinaleAsset)
                        });
                        if (trackPlan.FinaleAsset is not null)
                        {
                            AddAssetOutput(trackPlan.FinaleAsset);
                        }
                    }

                    generatedTracks.Add(assetKey, generatedForAsset);
                    AddAssetOutput(asset);
                    ReportEntryProgress();
                }
            }

            ReportProgress(
                options.Progress,
                MusicGenerationProgressStage.Writing,
                UiText.Get("Progress.GenerationIntegrating"),
                70);

            foreach (var entry in adoptedEntries.Where(entry =>
                         !processedAssetKeys.Contains(entry.AssetKey)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddAssetOutput(entry.Asset!);
                foreach (var finaleAsset in entry.Tracks
                             .Select(track => track.FinaleAsset)
                             .Where(asset => asset is not null)
                             .Cast<AssetSource>())
                {
                    AddAssetOutput(finaleAsset);
                }
                ReportEntryProgress();
            }

            var worldSpaceOutputs = worldSpaceTargets.Count == 0
                ? Array.Empty<GeneratedWorldSpaceOutput>()
                : AddWorldSpaceAssignments(
                    primaryPlugin,
                    worldSpaceTargets,
                    adoptedEntries,
                    generatedTracks,
                    planResolution,
                    cancellationToken);
            var integratedMusicTypes = integrationTargets.Length == 0
                ? Array.Empty<GeneratedMusicTypeOutput>()
                : AddIntegrationMusicTypes(
                    primaryPlugin,
                    integrationTargets,
                    generatedTracks,
                    cancellationToken);
            var cellOutputs = BuildCellOutputs(
                adoptedEntries,
                settingsByKey,
                planResolution,
                integratedMusicTypes);
            var cellSkyPatcherFileName = WriteCellSkyPatcherFile(
                stageDirectory,
                cellOutputs,
                cancellationToken);

            var mtdText = BuildMtd(
                planResolution,
                adoptedEntries,
                settings,
                settingsByKey,
                generatedTracks,
                options,
                worldSpaceTargets,
                integratedMusicTypes);
            var mtdPath = Path.Combine(stageDirectory, mtdFileName);
            File.WriteAllText(mtdPath, mtdText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var pluginOutputs = WritePlugins(
                pluginContexts,
                stageDirectory,
                worldSpaceOutputs,
                cancellationToken);
            var manifestPath = Path.Combine(stageDirectory, ManifestFileName);
            WriteManifest(
                manifestPath,
                outputDirectory,
                mtdFileName,
                plan,
                options,
                pluginOutputs,
                trackOutputs,
                worldSpaceOutputs,
                cellOutputs,
                integratedMusicTypes,
                cellSkyPatcherFileName,
                assetOutputs,
                capacity,
                cancellationToken);

            ReportProgress(
                options.Progress,
                MusicGenerationProgressStage.Diagnosing,
                UiText.Get("Progress.GenerationDiagnosing"),
                90);
            var diagnostic = _postGenerationDiagnostic.Run(
                stageDirectory,
                outputDirectory,
                mtdPath,
                manifestPath,
                settings,
                pluginOutputs,
                trackOutputs,
                worldSpaceOutputs,
                cellOutputs,
                integratedMusicTypes,
                cellSkyPatcherFileName,
                assetOutputs,
                options.WorldSpaceIndividualAssignment,
                capacity.NewRecordCount,
                capacity.MaxNewRecordsPerPlugin,
                planResolution,
                cancellationToken);
            GfMusicManagerLog.Info(
                $"Generation diagnostic: {diagnostic.Summary}, " +
                $"checks={diagnostic.CheckCount}, errors={diagnostic.ErrorCount}.");
            foreach (var diagnosticError in diagnostic.Errors)
            {
                GfMusicManagerLog.Error($"Generation diagnostic: {diagnosticError}");
            }

            if (!diagnostic.IsSuccess)
            {
                throw new MusicGenerationOutputException(
                    UiText.Format(
                        "Generation.Output.PostDiagnosticFailed",
                        diagnostic.Details));
            }

            CommitStage(stageDirectory, outputDirectory, options.OverwriteExisting);
            committed = true;
            ReportProgress(
                options.Progress,
                MusicGenerationProgressStage.Completed,
                UiText.Get("Progress.GenerationCompleted"),
                100);
            GfMusicManagerLog.Info(
                $"Generation: complete. output={outputDirectory}, tracks={trackOutputs.Count}, " +
                $"assets={assetOutputs.Count}, integratedTypes={integratedMusicTypes.Count}, " +
                $"worldSpaces={worldSpaceOutputs.Count}, cells={cellOutputs.Count}.");
            return new MusicGenerationOutputResult(
                outputDirectory,
                Path.Combine(outputDirectory, mtdFileName),
                Path.Combine(outputDirectory, ManifestFileName),
                pluginOutputs,
                trackOutputs,
                worldSpaceOutputs,
                cellOutputs,
                cellSkyPatcherFileName is null
                    ? null
                    : Path.Combine(outputDirectory, cellSkyPatcherFileName.Replace(
                        '\\',
                        Path.DirectorySeparatorChar)),
                assetOutputs,
                BuildWarnings(plan, capacity, options))
            {
                Diagnostic = diagnostic,
                IntegratedMusicTypes = integratedMusicTypes
            };
        }
        catch (OperationCanceledException)
        {
            GfMusicManagerLog.Warning("Generation: canceled; staging output will be removed.");
            throw;
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Exception("Generation failed", exception);
            throw;
        }
        finally
        {
            if (!committed)
            {
                TryDeleteDirectory(stageDirectory);
            }
        }
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

    private static void ValidateConditions(
        IReadOnlyList<MusicGenerationPlanEntry> adoptedEntries)
    {
        foreach (var entry in adoptedEntries)
        {
            var asset = entry.Asset!;
            foreach (var track in entry.Tracks)
            {
                try
                {
                    foreach (var condition in track.Conditions)
                    {
                        ValidateCondition(condition);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (MusicGenerationOutputException exception)
                {
                    throw new MusicGenerationOutputException(
                        MusicGenerationFailureContext.Append(
                            exception.Message,
                            asset,
                            track.TrackKey),
                        exception);
                }
                catch (Exception exception)
                {
                    throw new MusicGenerationOutputException(
                        MusicGenerationFailureContext.Append(
                            UiText.Format(
                                "Generation.Output.ConditionValidationFailed",
                                GenerationMessageFormatter.ExceptionDetail(exception)),
                            asset,
                            track.TrackKey),
                        exception);
                }
            }
        }
    }

    internal static void ValidateCondition(MusicConditionSource condition)
    {
        if (!MusicConditionSource.IsWritableFunction(condition.FunctionName))
        {
            throw new MusicGenerationOutputException(
                UiText.Format(
                    "Generation.Output.UneditableCondition",
                    condition.FunctionName,
                    condition.DataType));
        }

        if (!condition.ComparisonValueType.Equals(
                "Float",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new MusicGenerationOutputException(
                UiText.Format(
                    "Generation.Output.UnsupportedComparisonValue",
                    condition.FunctionName,
                    condition.ComparisonValueType));
        }

        if (!Enum.TryParse<CompareOperator>(condition.CompareOperator, true, out _))
        {
            throw new MusicGenerationOutputException(
                UiText.Format(
                    "Generation.Output.InvalidComparisonOperator",
                    condition.CompareOperator));
        }

        ParseConditionFlags(condition.Flags);
        ParseRunOnType(condition.RunOnType);
        if (!string.IsNullOrWhiteSpace(condition.ReferenceFormKey))
        {
            ParseFormKey(
                condition.ReferenceFormKey,
                UiText.Get("Generation.Label.ConditionTarget"));
        }

        if (condition.FunctionName.Equals(
                "GetCombatTargetHasKeyword",
                StringComparison.OrdinalIgnoreCase))
        {
            ParseFormKey(
                condition.KeywordFormKey,
                UiText.Get("Generation.Label.CombatKeyword"));
        }
        else if (condition.FunctionName.Equals(
                     "GetIsCurrentWeather",
                     StringComparison.OrdinalIgnoreCase))
        {
            ParseFormKey(
                condition.WeatherFormKey,
                UiText.Get("Generation.Label.Weather"));
        }
        else if (condition.FunctionName.Equals(
                     "GetInCurrentLoc",
                     StringComparison.OrdinalIgnoreCase))
        {
            ParseFormKey(
                condition.LocationFormKey,
                UiText.Get("Generation.Label.Location"));
        }
        else if (condition.FunctionName.Equals(
                     "GetInWorldspace",
                     StringComparison.OrdinalIgnoreCase))
        {
            ParseFormKey(
                condition.WorldspaceOrListFormKey,
                UiText.Get("Generation.Label.WorldSpace"));
        }
        else if (condition.FunctionName.Equals(
                     "GetInCurrentLocFormList",
                     StringComparison.OrdinalIgnoreCase))
        {
            ParseFormKey(
                condition.FormListFormKey,
                UiText.Get("Generation.Label.FormList"));
        }
        else if (condition.FunctionName.Equals(
                     "GetQuestCompleted",
                     StringComparison.OrdinalIgnoreCase))
        {
            ParseFormKey(
                condition.QuestFormKey,
                UiText.Get("Generation.Label.Quest"));
        }
        else if (condition.FunctionName.Equals(
                     "GetStageDone",
                     StringComparison.OrdinalIgnoreCase))
        {
            ParseFormKey(
                condition.QuestFormKey,
                UiText.Get("Generation.Label.Quest"));
            if (condition.StageIndex is null)
            {
                throw new MusicGenerationOutputException(
                    UiText.Format(
                        "Generation.Output.StageIndexMissing",
                        condition.FunctionName));
            }
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildTrackKeysByAssetKey(
        IReadOnlyList<MusicGenerationPlanEntry> entries)
    {
        var trackKeysByAssetKey = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase);

        void AddTrackKey(AssetSource? asset, string trackKey)
        {
            if (asset is null || string.IsNullOrWhiteSpace(trackKey))
            {
                return;
            }

            var assetKey = MusicGenerationPlanEntry.CreateAssetKey(asset);
            if (!trackKeysByAssetKey.TryGetValue(assetKey, out var trackKeys))
            {
                trackKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                trackKeysByAssetKey.Add(assetKey, trackKeys);
            }

            trackKeys.Add(trackKey);
        }

        foreach (var entry in entries)
        {
            foreach (var track in entry.Tracks)
            {
                AddTrackKey(entry.Asset, track.TrackKey);
                AddTrackKey(track.FinaleAsset, track.TrackKey);
            }
        }

        return trackKeysByAssetKey.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, MusicSettingSource> BuildSettingsIndex(
        IReadOnlyList<MusicSettingSource> settings)
    {
        var index = new Dictionary<string, MusicSettingSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in settings.GroupBy(
                     setting => CreateDestinationKey(MusicSettingKey.From(setting)),
                     StringComparer.OrdinalIgnoreCase))
        {
            index[group.Key] = SelectRepresentativeSetting(group);
        }

        return index;
    }

    private static MusicSettingSource SelectRepresentativeSetting(
        IEnumerable<MusicSettingSource> candidates) =>
        MusicTypeMtdAggregator.SelectRepresentativeSetting(candidates);

    private static IReadOnlyList<WorldSpaceTarget> BuildWorldSpaceTargets(
        IReadOnlyList<MusicGenerationPlanEntry> adoptedEntries,
        IReadOnlyDictionary<string, MusicSettingSource> settingsByKey,
        MusicGenerationOutputOptions options)
    {
        if (!options.WorldSpaceIndividualAssignment)
        {
            return Array.Empty<WorldSpaceTarget>();
        }

        var candidateKeys = adoptedEntries
            .SelectMany(entry => entry.DestinationKeys)
            .Where(destination => destination.Scope == MusicSettingScope.WorldSpace)
            .Select(destination => destination.ScopeFormKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selectedKeys = options.SelectedWorldSpaceFormKeys.Count == 0
            ? candidateKeys.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : options.SelectedWorldSpaceFormKeys;
        var selectedWithoutAdoptedTracks = selectedKeys
            .Except(candidateKeys, StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedWithoutAdoptedTracks.Length > 0)
        {
            throw new MusicGenerationOutputException(
                UiText.Format(
                    "Generation.Output.SelectedWorldSpaceNoAssets",
                    string.Join(", ", selectedWithoutAdoptedTracks)));
        }

        var targets = new List<WorldSpaceTarget>();
        foreach (var worldSpaceFormKey in candidateKeys)
        {
            if (!selectedKeys.Contains(worldSpaceFormKey))
            {
                continue;
            }

            var settingCandidates = adoptedEntries
                .SelectMany(entry => entry.DestinationKeys)
                .Where(destination =>
                    destination.Scope == MusicSettingScope.WorldSpace &&
                    destination.ScopeFormKey.Equals(
                        worldSpaceFormKey,
                        StringComparison.OrdinalIgnoreCase))
                .Select(destination => settingsByKey.TryGetValue(
                    CreateDestinationKey(destination),
                    out var source)
                    ? source
                    : null)
                .Where(source => source is not null)
                .Select(source => source!)
                .GroupBy(
                    source => source.MusicTypeFormKey,
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => SelectRepresentativeSetting(group))
                .ToArray();
            if (settingCandidates.Length == 0)
            {
                throw new MusicGenerationOutputException(
                    UiText.Format(
                        "Generation.Output.WorldSpaceAnalysisMissing",
                        worldSpaceFormKey));
            }

            var representativeSetting = SelectRepresentativeSetting(settingCandidates);
            var editorIdBase = string.IsNullOrWhiteSpace(representativeSetting.ScopeEditorId)
                ? representativeSetting.ScopeFormKey
                : representativeSetting.ScopeEditorId!;
            targets.Add(new WorldSpaceTarget(
                representativeSetting.ScopeFormKey,
                representativeSetting.ScopeEditorId,
                BuildGeneratedEditorId("MusicType", editorIdBase),
                representativeSetting,
                settingCandidates));
        }

        return targets;
    }

    private static void ValidateWorldSpaceConflicts(
        MusicGenerationPlanResolution planResolution,
        IReadOnlyList<WorldSpaceTarget> worldSpaceTargets,
        bool worldSpaceIndividualAssignment)
    {
        if (!worldSpaceIndividualAssignment)
        {
            return;
        }

        var selectedWorldSpaces = worldSpaceTargets
            .Select(target => target.FormKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unresolved = planResolution.IntegrationTargets
            .Where(target =>
                target.Scope == MusicSettingScope.WorldSpace &&
                !selectedWorldSpaces.Contains(target.ScopeFormKey))
            .ToArray();
        if (unresolved.Length == 0)
        {
            return;
        }

        var subjects = string.Join(
            ", ",
            unresolved.Select(target => target.ScopeEditorId ?? target.ScopeFormKey));
        throw new MusicGenerationOutputException(
            UiText.Format(
                "Generation.Output.WorldSpaceRecordRequired",
                subjects));
    }

    private static IReadOnlyList<GeneratedMusicTypeOutput> AddIntegrationMusicTypes(
        GenerationPluginContext primaryPlugin,
        IReadOnlyList<MusicGenerationIntegrationTarget> targets,
        IReadOnlyDictionary<string, IReadOnlyList<IMusicTrack>> generatedTracks,
        CancellationToken cancellationToken)
    {
        var outputs = new List<GeneratedMusicTypeOutput>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var musicType = primaryPlugin.Mod.MusicTypes.AddNew(
                target.GeneratedMusicTypeEditorId);
            musicType.Tracks = new();
            var finalTrackFormKeys = new List<FormKey>();
            var finalTrackFormKeySet = new HashSet<FormKey>();

            foreach (var sourceTrack in target.OfficialTracks)
            {
                if (TryParseFormKey(sourceTrack.FormKey, out var formKey) &&
                    finalTrackFormKeySet.Add(formKey))
                {
                    musicType.Tracks.Add(new FormLink<IMusicTrackGetter>(formKey));
                    finalTrackFormKeys.Add(formKey);
                }
            }

            foreach (var entry in target.GeneratedEntries)
            {
                if (!generatedTracks.TryGetValue(entry.AssetKey, out var generatedForAsset))
                {
                    throw new MusicGenerationOutputException(
                        UiText.Format(
                            "Generation.Output.IntegratedTrackMissing",
                            entry.AssetKey));
                }

                foreach (var generatedTrack in generatedForAsset)
                {
                    if (finalTrackFormKeySet.Add(generatedTrack.FormKey))
                    {
                        musicType.Tracks.Add(
                            new FormLink<IMusicTrackGetter>(generatedTrack.FormKey));
                        finalTrackFormKeys.Add(generatedTrack.FormKey);
                    }
                }
            }

            outputs.Add(new GeneratedMusicTypeOutput(
                target.TargetKey,
                target.Scope,
                target.ScopeFormKey,
                target.ScopeEditorId,
                target.GeneratedMusicTypeEditorId,
                musicType.FormKey.ToString(),
                primaryPlugin.PluginFileName,
                finalTrackFormKeys
                    .Select(formKey => formKey.ToString())
                    .ToArray()));
        }

        return outputs;
    }

    private static IReadOnlyList<GeneratedCellOutput> BuildCellOutputs(
        IReadOnlyList<MusicGenerationPlanEntry> adoptedEntries,
        IReadOnlyDictionary<string, MusicSettingSource> settingsByKey,
        MusicGenerationPlanResolution planResolution,
        IReadOnlyList<GeneratedMusicTypeOutput> integratedMusicTypes)
    {
        var outputsByCell = new Dictionary<string, GeneratedCellOutput>(
            StringComparer.OrdinalIgnoreCase);
        var integratedByTarget = integratedMusicTypes.ToDictionary(
            output => output.TargetKey,
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in adoptedEntries)
        {
            foreach (var destination in entry.DestinationKeys.Where(destination =>
                         destination.Scope == MusicSettingScope.Cell))
            {
                if (!settingsByKey.TryGetValue(
                        CreateDestinationKey(destination),
                        out var setting))
                {
                    throw new MusicGenerationOutputException(
                        UiText.Format(
                            "Generation.Output.CellDefinitionMissing",
                            CreateDestinationKey(destination)));
                }

                var musicTypeFormKey = setting.MusicTypeFormKey;
                var musicTypeEditorId = setting.MusicTypeEditorId;
                if (planResolution.TryGetIntegrationTarget(
                        destination.Scope,
                        destination.ScopeFormKey,
                        out var integrationTarget) &&
                    integratedByTarget.TryGetValue(
                        integrationTarget.TargetKey,
                        out var integratedMusicType))
                {
                    musicTypeFormKey = integratedMusicType.MusicTypeFormKey;
                    musicTypeEditorId = integratedMusicType.MusicTypeEditorId;
                }

                outputsByCell[destination.ScopeFormKey] = new GeneratedCellOutput(
                    destination.ScopeFormKey,
                    setting.ScopeEditorId,
                    musicTypeFormKey,
                    musicTypeEditorId);
            }
        }

        return outputsByCell.Values
            .OrderBy(output => output.CellFormKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? WriteCellSkyPatcherFile(
        string stageDirectory,
        IReadOnlyList<GeneratedCellOutput> cells,
        CancellationToken cancellationToken)
    {
        if (cells.Count == 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var relativePath = MusicCellSkyPatcherOutput.RelativeFilePath;
        var path = Path.Combine(
            stageDirectory,
            relativePath.Replace('\\', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new MusicGenerationOutputException(
                UiText.Format(
                    "Generation.Output.CellSkyPatcherPathMissing",
                    relativePath));
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(
            path,
            MusicCellSkyPatcherOutput.BuildIni(cells),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        GfMusicManagerLog.Info(
            $"Generation: Cell SkyPatcher output written. path={relativePath}, cells={cells.Count}.");
        return relativePath;
    }

    private static IReadOnlyList<GenerationPluginContext> CreatePluginContexts(
        IReadOnlyList<MusicGenerationPluginEstimate> estimates)
    {
        return estimates
            .Select(estimate => CreatePluginContext(estimate.PluginFileName))
            .ToArray();
    }

    private static GenerationPluginContext CreatePluginContext(string pluginFileName)
    {
        var mod = new SkyrimMod(
            ModKey.FromNameAndExtension(pluginFileName),
            SkyrimRelease.SkyrimSE,
            headerVersion: null,
            forceUseLowerFormIDRanges: true);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
        ((Mutagen.Bethesda.Plugins.Records.IMod)mod).NextFormID = 1;
        return new GenerationPluginContext(pluginFileName, mod);
    }

    private static IMusicTrack AddMusicTrack(
        SkyrimMod mod,
        AssetSource asset,
        MusicGenerationTrackPlan trackPlan,
        IReadOnlyList<MusicConditionSource> conditions,
        MusicGenerationAssetPathMap assetPathMap,
        ISet<string> usedEditorIds)
    {
        var contextAsset = asset;
        var contextVirtualPath = asset.VirtualPath;
        try
        {
            var editorId = GeneratedMusicTrackEditorId.Create(
                asset.VirtualPath,
                usedEditorIds);
            var track = mod.MusicTracks.AddNew(editorId);
            track.Type = MusicTrack.TypeEnum.SingleTrack;
            track.TrackFilename = new();
            var outputVirtualPath = assetPathMap.GetOutputPath(asset);
            if (!track.TrackFilename.TrySetPath(outputVirtualPath))
            {
                throw new MusicGenerationOutputException(
                    UiText.Format(
                        "Generation.Output.TrackAudioPathMissing",
                        outputVirtualPath));
            }

            if (!string.IsNullOrWhiteSpace(trackPlan.FinaleAudioPath))
            {
                contextAsset = trackPlan.FinaleAsset ?? asset;
                contextVirtualPath = trackPlan.FinaleAsset is null
                    ? trackPlan.FinaleAudioPath
                    : assetPathMap.GetOutputPath(trackPlan.FinaleAsset);
                track.FinaleFilename = new();
                if (!track.FinaleFilename.TrySetPath(
                        contextVirtualPath))
                {
                    throw new MusicGenerationOutputException(
                        UiText.Format(
                            "Generation.Output.FinaleAudioPathMissing",
                            contextVirtualPath));
                }
            }

            // Generated tracks are rebuilt from the adopted audio and conditions.
            // Do not carry source cue points into the generated record; this also
            // repairs AMP's combat-track cue-point issue without editing the source.
            contextAsset = asset;
            contextVirtualPath = asset.VirtualPath;
            track.CuePoints = new();
            track.Conditions = new();
            foreach (var condition in conditions)
            {
                track.Conditions.Add(CreateCondition(condition));
            }

            return track;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MusicGenerationOutputException exception)
        {
            throw new MusicGenerationOutputException(
                MusicGenerationFailureContext.Append(
                    exception.Message,
                    contextAsset,
                    trackPlan.TrackKey,
                    contextVirtualPath),
                exception);
        }
        catch (Exception exception)
        {
            throw new MusicGenerationOutputException(
                MusicGenerationFailureContext.Append(
                    UiText.Format(
                        "Generation.Output.TrackWriteFailed",
                        GenerationMessageFormatter.ExceptionDetail(exception)),
                    contextAsset,
                    trackPlan.TrackKey,
                    contextVirtualPath),
                exception);
        }
    }

    private static Condition CreateCondition(MusicConditionSource source)
    {
        if (!Enum.TryParse<CompareOperator>(source.CompareOperator, true, out var compareOperator))
        {
            throw new MusicGenerationOutputException(
                UiText.Format(
                    "Generation.Output.InvalidComparisonOperator",
                    source.CompareOperator));
        }

        var condition = source.FunctionName.ToLowerInvariant() switch
        {
            "getcurrenttime" => CreateCurrentTimeCondition(
                source,
                compareOperator),
            "getcombattargethaskeyword" => CreateCombatKeywordCondition(
                source,
                compareOperator),
            "getiscurrentweather" => CreateWeatherCondition(
                source,
                compareOperator),
            "getincurrentloc" => CreateCurrentLocationCondition(
                source,
                compareOperator),
            "getinworldspace" => CreateWorldspaceCondition(
                source,
                compareOperator),
            "getincurrentlocformlist" => CreateCurrentLocationFormListCondition(
                source,
                compareOperator),
            "getquestcompleted" => CreateQuestCompletedCondition(
                source,
                compareOperator),
            "getstagedone" => CreateStageDoneCondition(
                source,
                compareOperator),
            _ => throw new MusicGenerationOutputException(
                UiText.Format(
                    "Generation.Output.ConditionNotSupportedInOutput",
                    source.FunctionName,
                    source.DataType))
        };
        condition.Flags = ParseConditionFlags(source.Flags);
        return condition;
    }

    private static ConditionFloat CreateCurrentTimeCondition(
        MusicConditionSource source,
        CompareOperator compareOperator)
    {
        var data = new GetCurrentTimeConditionData();
        ApplyConditionDataMetadata(data, source);
        data.FirstUnusedIntParameter = source.FirstUnusedIntParameter ?? 0;
        data.SecondUnusedIntParameter = source.SecondUnusedIntParameter ?? 0;
        data.FirstUnusedStringParameter = source.FirstUnusedStringParameter ?? string.Empty;
        data.SecondUnusedStringParameter = source.SecondUnusedStringParameter ?? string.Empty;
        return CreateFloatCondition(source, compareOperator, data);
    }

    private static Condition CreateCombatKeywordCondition(
        MusicConditionSource source,
        CompareOperator compareOperator)
    {
        var data = new GetCombatTargetHasKeywordConditionData();
        ApplyConditionDataMetadata(data, source);
        data.Keyword = new FormLinkOrIndex<IKeywordGetter>(
            data,
            ParseFormKey(
                source.KeywordFormKey,
                UiText.Get("Generation.Label.CombatKeyword")));
        data.SecondUnusedIntParameter = source.SecondUnusedIntParameter ?? 0;
        data.FirstUnusedStringParameter = source.FirstUnusedStringParameter ?? string.Empty;
        data.SecondUnusedStringParameter = source.SecondUnusedStringParameter ?? string.Empty;
        return CreateFloatCondition(source, compareOperator, data);
    }

    private static Condition CreateWeatherCondition(
        MusicConditionSource source,
        CompareOperator compareOperator)
    {
        var data = new GetIsCurrentWeatherConditionData();
        ApplyConditionDataMetadata(data, source);
        data.Weather = new FormLinkOrIndex<IWeatherGetter>(
            data,
            ParseFormKey(
                source.WeatherFormKey,
                UiText.Get("Generation.Label.Weather")));
        data.SecondUnusedIntParameter = source.SecondUnusedIntParameter ?? 0;
        data.FirstUnusedStringParameter = source.FirstUnusedStringParameter ?? string.Empty;
        data.SecondUnusedStringParameter = source.SecondUnusedStringParameter ?? string.Empty;
        return CreateFloatCondition(source, compareOperator, data);
    }

    private static Condition CreateCurrentLocationCondition(
        MusicConditionSource source,
        CompareOperator compareOperator)
    {
        var data = new GetInCurrentLocConditionData();
        ApplyConditionDataMetadata(data, source);
        data.Location = new FormLinkOrIndex<ILocationGetter>(
            data,
            ParseFormKey(
                source.LocationFormKey,
                UiText.Get("Generation.Label.Location")));
        data.SecondUnusedIntParameter = source.SecondUnusedIntParameter ?? 0;
        data.FirstUnusedStringParameter = source.FirstUnusedStringParameter ?? string.Empty;
        data.SecondUnusedStringParameter = source.SecondUnusedStringParameter ?? string.Empty;
        return CreateFloatCondition(source, compareOperator, data);
    }

    private static Condition CreateWorldspaceCondition(
        MusicConditionSource source,
        CompareOperator compareOperator)
    {
        var data = new GetInWorldspaceConditionData();
        ApplyConditionDataMetadata(data, source);
        data.WorldspaceOrList = new FormLinkOrIndex<IWorldspaceOrListGetter>(
            data,
            ParseFormKey(
                source.WorldspaceOrListFormKey,
                UiText.Get("Generation.Label.WorldSpace")));
        data.SecondUnusedIntParameter = source.SecondUnusedIntParameter ?? 0;
        data.FirstUnusedStringParameter = source.FirstUnusedStringParameter ?? string.Empty;
        data.SecondUnusedStringParameter = source.SecondUnusedStringParameter ?? string.Empty;
        return CreateFloatCondition(source, compareOperator, data);
    }

    private static Condition CreateCurrentLocationFormListCondition(
        MusicConditionSource source,
        CompareOperator compareOperator)
    {
        var data = new GetInCurrentLocFormListConditionData();
        ApplyConditionDataMetadata(data, source);
        data.FormList = new FormLinkOrIndex<IFormListGetter>(
            data,
            ParseFormKey(
                source.FormListFormKey,
                UiText.Get("Generation.Label.FormList")));
        data.SecondUnusedIntParameter = source.SecondUnusedIntParameter ?? 0;
        data.FirstUnusedStringParameter = source.FirstUnusedStringParameter ?? string.Empty;
        data.SecondUnusedStringParameter = source.SecondUnusedStringParameter ?? string.Empty;
        return CreateFloatCondition(source, compareOperator, data);
    }

    private static Condition CreateQuestCompletedCondition(
        MusicConditionSource source,
        CompareOperator compareOperator)
    {
        var data = new GetQuestCompletedConditionData();
        ApplyConditionDataMetadata(data, source);
        data.Quest = new FormLinkOrIndex<IQuestGetter>(
            data,
            ParseFormKey(
                source.QuestFormKey,
                UiText.Get("Generation.Label.Quest")));
        data.SecondUnusedIntParameter = source.SecondUnusedIntParameter ?? 0;
        data.FirstUnusedStringParameter = source.FirstUnusedStringParameter ?? string.Empty;
        data.SecondUnusedStringParameter = source.SecondUnusedStringParameter ?? string.Empty;
        return CreateFloatCondition(source, compareOperator, data);
    }

    private static Condition CreateStageDoneCondition(
        MusicConditionSource source,
        CompareOperator compareOperator)
    {
        var data = new GetStageDoneConditionData();
        ApplyConditionDataMetadata(data, source);
        data.Quest = new FormLinkOrIndex<IQuestGetter>(
            data,
            ParseFormKey(
                source.QuestFormKey,
                UiText.Get("Generation.Label.Quest")));
        data.Stage = source.StageIndex ?? throw new MusicGenerationOutputException(
            UiText.Format(
                "Generation.Output.StageIndexMissing",
                source.FunctionName));
        data.FirstUnusedStringParameter = source.FirstUnusedStringParameter ?? string.Empty;
        data.SecondUnusedStringParameter = source.SecondUnusedStringParameter ?? string.Empty;
        return CreateFloatCondition(source, compareOperator, data);
    }

    private static ConditionFloat CreateFloatCondition(
        MusicConditionSource source,
        CompareOperator compareOperator,
        ConditionData data)
    {
        return new ConditionFloat
        {
            CompareOperator = compareOperator,
            ComparisonValue = source.ComparisonValue,
            Data = data
        };
    }

    private static void ApplyConditionDataMetadata(
        ConditionData data,
        MusicConditionSource source)
    {
        data.RunOnType = ParseRunOnType(source.RunOnType);
        data.RunOnTypeIndex = source.RunOnTypeIndex;
        data.UseAliases = source.UseAliases;
        data.UsePackageData = source.UsePackageData;
        if (!string.IsNullOrWhiteSpace(source.ReferenceFormKey))
        {
            data.Reference = new FormLink<ISkyrimMajorRecordGetter>(
                ParseFormKey(
                    source.ReferenceFormKey,
                    UiText.Get("Generation.Label.ConditionTarget")));
        }
    }

    private static Condition.Flag ParseConditionFlags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (Enum.TryParse<Condition.Flag>(value, true, out var flags))
        {
            return flags;
        }

        throw new MusicGenerationOutputException(
            UiText.Format("Generation.Output.InvalidConditionFlags", value));
    }

    private static Condition.RunOnType ParseRunOnType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Condition.RunOnType.Subject;
        }

        if (Enum.TryParse<Condition.RunOnType>(value, true, out var runOnType))
        {
            return runOnType;
        }

        throw new MusicGenerationOutputException(
            UiText.Format("Generation.Output.InvalidRunOnType", value));
    }

    private static FormKey ParseFormKey(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MusicGenerationOutputException(
                UiText.Format("Generation.Output.FormIdMissing", label));
        }

        try
        {
            return FormKey.Factory(value);
        }
        catch
        {
            throw new MusicGenerationOutputException(
                UiText.Format("Generation.Output.FormIdInvalid", label, value));
        }
    }

    private IReadOnlyList<GeneratedWorldSpaceOutput> AddWorldSpaceAssignments(
        GenerationPluginContext primaryPlugin,
        IReadOnlyList<WorldSpaceTarget> targets,
        IReadOnlyList<MusicGenerationPlanEntry> adoptedEntries,
        IReadOnlyDictionary<string, IReadOnlyList<IMusicTrack>> generatedTracks,
        MusicGenerationPlanResolution planResolution,
        CancellationToken cancellationToken)
    {
        var outputs = new List<GeneratedWorldSpaceOutput>(targets.Count);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var musicTypeEditorId = planResolution.TryGetIntegrationTarget(
                    MusicSettingScope.WorldSpace,
                    target.FormKey,
                    out var integrationTarget)
                ? integrationTarget.GeneratedMusicTypeEditorId
                : target.MusicTypeEditorId;
            var musicType = primaryPlugin.Mod.MusicTypes.AddNew(musicTypeEditorId);
            musicType.Tracks = new();
            var finalTrackFormKeys = new List<FormKey>();
            var finalTrackFormKeySet = new HashSet<FormKey>();
            foreach (var setting in target.Settings)
            {
                if (!planResolution.TryGetMusicType(
                        setting.MusicTypeFormKey,
                        out var resolvedMusicType))
                {
                    continue;
                }

                foreach (var sourceTrack in resolvedMusicType.OfficialTracks)
                {
                    if (TryParseFormKey(sourceTrack.FormKey, out var formKey) &&
                        finalTrackFormKeySet.Add(formKey))
                    {
                        musicType.Tracks.Add(new FormLink<IMusicTrackGetter>(formKey));
                        finalTrackFormKeys.Add(formKey);
                    }
                }
            }

            var outputTrackKeys = adoptedEntries
                .Where(entry => entry.IsAdopted)
                .Where(entry => entry.DestinationKeys.Any(destination =>
                    destination.Scope == MusicSettingScope.WorldSpace &&
                    destination.ScopeFormKey.Equals(
                        target.FormKey,
                        StringComparison.OrdinalIgnoreCase)))
                .Select(entry => entry.AssetKey)
                .Where(generatedTracks.ContainsKey)
                .SelectMany(assetKey => generatedTracks[assetKey])
                .Select(track => track.FormKey)
                .ToArray();
            foreach (var trackFormKey in outputTrackKeys)
            {
                if (finalTrackFormKeySet.Add(trackFormKey))
                {
                    musicType.Tracks.Add(new FormLink<IMusicTrackGetter>(trackFormKey));
                    finalTrackFormKeys.Add(trackFormKey);
                }
            }

            var worldspace = LoadWorldSpaceOverride(target, primaryPlugin.Mod);
            worldspace.Music = new FormLinkNullable<IMusicTypeGetter>(musicType.FormKey);
            outputs.Add(new GeneratedWorldSpaceOutput(
                target.FormKey,
                target.EditorId,
                musicTypeEditorId,
                musicType.FormKey.ToString(),
                primaryPlugin.PluginFileName,
                finalTrackFormKeys.Select(formKey => formKey.ToString()).ToArray()));
        }

        return outputs;
    }

    private static IWorldspace LoadWorldSpaceOverride(
        WorldSpaceTarget target,
        SkyrimMod outputMod)
    {
        var pluginSource = target.RepresentativeSetting.Record.Plugin;
        if (!File.Exists(pluginSource.Path))
        {
            throw new MusicGenerationOutputException(UiText.Format(
                "Generation.Output.WorldSpaceSourcePluginMissing",
                pluginSource.Path));
        }

        var modKey = ModKey.FromNameAndExtension(pluginSource.Name);
        using var sourceMod = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(modKey, pluginSource.Path),
            SkyrimRelease.SkyrimSE,
            new BinaryReadParameters());
        var formKey = ParseFormKey(target.FormKey, "WorldSpace");
        var sourceWorldspace = sourceMod.Worldspaces.Records
            .FirstOrDefault(worldspace => worldspace.FormKey == formKey);
        if (sourceWorldspace is null)
        {
            throw new MusicGenerationOutputException(UiText.Format(
                "Generation.Output.WorldSpaceRecordMissing",
                target.FormKey));
        }

        return outputMod.Worldspaces.GetOrAddAsOverride(sourceWorldspace);
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

    private static string BuildMtd(
        MusicGenerationPlanResolution planResolution,
        IReadOnlyList<MusicGenerationPlanEntry> adoptedEntries,
        IReadOnlyList<MusicSettingSource> settings,
        IReadOnlyDictionary<string, MusicSettingSource> settingsByKey,
        IReadOnlyDictionary<string, IReadOnlyList<IMusicTrack>> generatedTracks,
        MusicGenerationOutputOptions options,
        IReadOnlyList<WorldSpaceTarget> worldSpaceTargets,
        IReadOnlyList<GeneratedMusicTypeOutput> integratedMusicTypes)
    {
        var general = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var locations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var regions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var integratedByTarget = integratedMusicTypes.ToDictionary(
            output => output.TargetKey,
            StringComparer.OrdinalIgnoreCase);
        var selectedWorldSpaces = worldSpaceTargets
            .Select(target => target.FormKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var mtdAggregation = MusicTypeMtdAggregator.Build(
            planResolution,
            adoptedEntries,
            settings,
            options.WorldSpaceIndividualAssignment,
            selectedWorldSpaces);
        foreach (var musicTypeFormKey in mtdAggregation.MissingDefinitionFormKeys)
        {
            throw new MusicGenerationOutputException(UiText.Format(
                "Generation.Output.MusicTypeDefinitionMissing",
                musicTypeFormKey));
        }

        foreach (var musicTypeFormKey in mtdAggregation.MissingEditorIdFormKeys)
        {
            throw new MusicGenerationOutputException(UiText.Format(
                "Generation.Output.MusicTypeEditorIdMissing",
                musicTypeFormKey));
        }

        foreach (var aggregate in mtdAggregation.Aggregates)
        {
            var finalTrackIds = new List<string>();
            foreach (var officialTrack in aggregate.OfficialTracks)
            {
                finalTrackIds.Add(FormatMtdFormId(
                    ParseFormKey(
                        officialTrack.FormKey,
                        UiText.Get("Generation.Label.OfficialMusicTrack"))));
            }

            foreach (var generatedEntry in aggregate.GeneratedEntries)
            {
                if (!generatedTracks.TryGetValue(
                        generatedEntry.AssetKey,
                        out var generatedForAsset))
                {
                    throw new MusicGenerationOutputException(UiText.Format(
                        "Generation.Output.IntegratedTrackMissing",
                        generatedEntry.AssetKey));
                }

                finalTrackIds.AddRange(generatedForAsset.Select(track =>
                    FormatMtdFormId(track.FormKey)));
            }

            general[aggregate.MusicTypeEditorId] = finalTrackIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        foreach (var entry in adoptedEntries)
        {
            foreach (var destination in entry.DestinationKeys)
            {
                if (!settingsByKey.TryGetValue(CreateDestinationKey(destination), out var setting))
                {
                    throw new MusicGenerationOutputException(UiText.Format(
                        "Generation.Output.MusicTypeDefinitionMissing",
                        CreateDestinationKey(destination)));
                }

                switch (destination.Scope)
                {
                    case MusicSettingScope.MusicType:
                        break;
                    case MusicSettingScope.Location:
                        AddLocationOrRegion(
                            locations,
                            setting.ScopeEditorId,
                            ResolveFinalMusicTypeEditorId(
                                destination,
                                setting,
                                planResolution,
                                integratedByTarget));
                        break;
                    case MusicSettingScope.Region:
                        AddLocationOrRegion(
                            regions,
                            setting.ScopeEditorId,
                            ResolveFinalMusicTypeEditorId(
                                destination,
                                setting,
                                planResolution,
                                integratedByTarget));
                        break;
                    case MusicSettingScope.WorldSpace:
                        if (options.WorldSpaceIndividualAssignment &&
                            selectedWorldSpaces.Contains(destination.ScopeFormKey))
                        {
                            // The selected WorldSpace is represented by an ESP override.
                        }
                        break;
                    case MusicSettingScope.Cell:
                        // Cell is written separately as a SkyPatcher runtime assignment.
                        break;
                    default:
                        throw new MusicGenerationOutputException(UiText.Format(
                            "Generation.Output.UnsupportedSettingScope",
                            destination.Scope));
                }
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine("; Generated by GF Music Manager");
        builder.AppendLine("; Generated Music Track records are stored in the GF Music Product ESP.");
        builder.AppendLine();
        WriteMtdSection(
            builder,
            "General",
            general.ToDictionary(
                pair => FormatMtdKey(
                    pair.Key,
                    replaceExisting: true),
                pair => string.Join(',', pair.Value),
                StringComparer.OrdinalIgnoreCase));
        WriteMtdSection(builder, "Location", locations);
        WriteMtdSection(builder, "Region", regions);
        return builder.ToString();
    }

    private static string? ResolveFinalMusicTypeEditorId(
        MusicSettingKey destination,
        MusicSettingSource setting,
        MusicGenerationPlanResolution planResolution,
        IReadOnlyDictionary<string, GeneratedMusicTypeOutput> integratedByTarget)
    {
        if (planResolution.TryGetIntegrationTarget(
                destination.Scope,
                destination.ScopeFormKey,
                out var integrationTarget) &&
            integratedByTarget.TryGetValue(
                integrationTarget.TargetKey,
                out var integratedMusicType))
        {
            return integratedMusicType.MusicTypeEditorId;
        }

        return setting.MusicTypeEditorId;
    }

    private static void AddLocationOrRegion(
        IDictionary<string, string> destinations,
        string? scopeEditorId,
        string? musicTypeEditorId)
    {
        if (string.IsNullOrWhiteSpace(scopeEditorId) ||
            string.IsNullOrWhiteSpace(musicTypeEditorId))
        {
            throw new MusicGenerationOutputException(
                UiText.Get("Generation.Output.AssignmentEditorIdMissing"));
        }

        destinations[scopeEditorId] = musicTypeEditorId;
    }

    private static void WriteMtdSection(
        StringBuilder builder,
        string sectionName,
        IReadOnlyDictionary<string, string> values)
    {
        builder.AppendLine($"[{sectionName}]");
        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key).Append(" = ").AppendLine(pair.Value);
        }

        builder.AppendLine();
    }

    private static string FormatMtdKey(string editorId, bool replaceExisting) =>
        replaceExisting ? editorId + "!" : editorId;

    private static string FormatMtdFormId(FormKey formKey) =>
        $"0x{formKey.ID:X6}~{formKey.ModKey.FileName}";

    private static IReadOnlyList<GeneratedPluginOutput> WritePlugins(
        IReadOnlyList<GenerationPluginContext> contexts,
        string stageDirectory,
        IReadOnlyList<GeneratedWorldSpaceOutput> worldSpaceOutputs,
        CancellationToken cancellationToken)
    {
        var outputs = new List<GeneratedPluginOutput>(contexts.Count);
        foreach (var context in contexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
            var pluginPath = Path.Combine(stageDirectory, context.PluginFileName);
            using var stream = File.Create(pluginPath);
            context.Mod.WriteToBinary(stream, new BinaryWriteParameters
            {
                MinimumFormID = new ForceLowerFormIdRangeOption
                {
                    ForceLowerRangeSetting = true
                },
                LowerRangeDisallowedHandler = new NoCheckIfLowerRangeDisallowed()
            });
            outputs.Add(new GeneratedPluginOutput(
                context.PluginFileName,
                context.Mod.MusicTracks.Count,
                context.Mod.MusicTypes.Count,
                worldSpaceOutputs.Count(output =>
                    output.PluginFileName.Equals(
                        context.PluginFileName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                NewSupportRecordCount = context.Mod.FormLists.Count
            });
        }

        return outputs;
    }

    private static void WriteManifest(
        string manifestPath,
        string outputDirectory,
        string mtdFileName,
        MusicGenerationPlan plan,
        MusicGenerationOutputOptions options,
        IReadOnlyList<GeneratedPluginOutput> plugins,
        IReadOnlyList<GeneratedMusicTrackOutput> tracks,
        IReadOnlyList<GeneratedWorldSpaceOutput> worldSpaces,
        IReadOnlyList<GeneratedCellOutput> cells,
        IReadOnlyList<GeneratedMusicTypeOutput> integratedMusicTypes,
        string? cellSkyPatcherFileName,
        IReadOnlyList<GeneratedAssetOutput> assets,
        MusicGenerationCapacityEstimate capacity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = new MusicGenerationManifest(
            5,
            DateTimeOffset.UtcNow,
            outputDirectory,
            mtdFileName,
            options.WorldSpaceIndividualAssignment,
            plan.KeepVanillaMusic == true,
            plugins,
            tracks,
            worldSpaces,
            cells,
            integratedMusicTypes,
            cellSkyPatcherFileName,
            assets,
            plan.Entries
                .Select(entry => new MusicGenerationPlanEntryOutput(
                    entry.AssetKey,
                    entry.Asset?.VirtualPath ?? string.Empty,
                    entry.IsAdopted,
                    entry.DestinationKeys
                        .Select(CreateDestinationKey)
                        .ToArray(),
                    entry.Conditions)
                {
                    Tracks = entry.Tracks
                        .Select(track => new MusicGenerationTrackPlanOutput(
                            track.TrackKey,
                            track.Conditions))
                        .ToArray()
                })
                .ToArray(),
            capacity.NewRecordCount,
            capacity.MaxNewRecordsPerPlugin);
        var json = JsonSerializer.Serialize(
            manifest,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(
            manifestPath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static IReadOnlyList<string> BuildWarnings(
        MusicGenerationPlan plan,
        MusicGenerationCapacityEstimate capacity,
        MusicGenerationOutputOptions options)
    {
        var warnings = plan.Conflicts
            .Where(conflict =>
                conflict.Kind == MusicGenerationPlanConflictKind.MultipleGeneratedMusicTypesForRecord &&
                (options.WorldSpaceIndividualAssignment ||
                 conflict.TargetScope != MusicSettingScope.WorldSpace))
            .Select(conflict => conflict.Message)
            .ToList();
        if (capacity.RequiresSplit)
        {
            warnings.Add(UiText.Format(
                "Generation.Warning.PluginSplit",
                capacity.Plugins.Count));
        }

        if (options.WorldSpaceIndividualAssignment)
        {
            warnings.Add(UiText.Get("Generation.Warning.WorldSpaceOverrides"));
        }

        return warnings;
    }

    private GeneratedAssetOutput CopyAsset(
        AssetSource asset,
        MusicGenerationAssetPathMap assetPathMap,
        string stageDirectory,
        CancellationToken cancellationToken,
        IReadOnlyList<string> trackKeys)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = assetPathMap.GetOutputPath(asset);
            var destinationPath = Path.Combine(
                stageDirectory,
                virtualPath.Replace('\\', Path.DirectorySeparatorChar));
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                throw new MusicGenerationOutputException(UiText.Format(
                    "Generation.Output.AssetDirectoryMissing",
                    virtualPath));
            }

            if (!assetPathMap.IsRemapped(asset) && asset.IsVfsWinner && asset.ModEnabled)
            {
                var referencedLength = asset.Length ?? 0;
                GfMusicManagerLog.Info(
                    $"Generation: asset referenced. virtualPath={virtualPath}, source={asset.SourcePath}.");
                return new GeneratedAssetOutput(
                    MusicGenerationPlanEntry.CreateAssetKey(asset),
                    virtualPath,
                    asset.SourceKind,
                    asset.SourcePath,
                    asset.ArchiveEntryPath,
                    null,
                    referencedLength,
                    false)
                {
                    ModName = asset.ModName,
                    TrackKeys = trackKeys
                };
            }

            Directory.CreateDirectory(destinationDirectory);
            if (asset.SourceKind == AssetSourceKind.Loose)
            {
                if (!File.Exists(asset.SourcePath))
                {
                    throw new MusicGenerationOutputException(UiText.Format(
                        "Generation.Output.LooseAssetMissing",
                        asset.SourcePath));
                }

                File.Copy(asset.SourcePath, destinationPath, overwrite: false);
            }
            else
            {
                var bytes = _archiveReader.ReadEntry(
                    asset.SourcePath,
                    asset.ArchiveEntryPath ?? asset.VirtualPath);
                File.WriteAllBytes(destinationPath, bytes);
            }

            var length = new FileInfo(destinationPath).Length;
            GfMusicManagerLog.Info(
                $"Generation: asset copied. kind={asset.SourceKind}, virtualPath={virtualPath}, bytes={length}.");
            return new GeneratedAssetOutput(
                MusicGenerationPlanEntry.CreateAssetKey(asset),
                virtualPath,
                asset.SourceKind,
                asset.SourcePath,
                asset.ArchiveEntryPath,
                virtualPath,
                length,
                true)
            {
                ModName = asset.ModName,
                TrackKeys = trackKeys
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MusicGenerationOutputException exception)
        {
            throw new MusicGenerationOutputException(
                MusicGenerationFailureContext.Append(
                    exception.Message,
                    asset.ModName,
                    trackKeys,
                    asset.VirtualPath),
                exception);
        }
        catch (Exception exception)
        {
            throw new MusicGenerationOutputException(
                MusicGenerationFailureContext.Append(
                    UiText.Format(
                        "Generation.Output.AssetWriteFailed",
                        GenerationMessageFormatter.ExceptionDetail(exception)),
                    asset.ModName,
                    trackKeys,
                    asset.VirtualPath),
                exception);
        }
    }

    private static void CommitStage(
        string stageDirectory,
        string outputDirectory,
        bool overwriteExisting)
    {
        if (Directory.Exists(outputDirectory))
        {
            if (!overwriteExisting)
            {
                throw new MusicGenerationOutputException(UiText.Format(
                    "Generation.Output.OutputExists",
                    outputDirectory));
            }

            Directory.Delete(outputDirectory, recursive: true);
        }

        Directory.Move(stageDirectory, outputDirectory);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception)
        {
            GfMusicManagerLog.Warning($"Generation: failed to remove staging directory: {exception}");
        }
    }

    private static string NormalizeVirtualPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new MusicGenerationOutputException(
                UiText.Get("Generation.Output.AudioPathEmpty"));
        }

        var normalized = path.Replace('/', '\\').TrimStart('\\');
        if (Path.IsPathRooted(normalized) ||
            normalized.Split('\\').Any(part => part is "." or ".."))
        {
            throw new MusicGenerationOutputException(UiText.Format(
                "Generation.Output.AudioPathOutsideMod",
                path));
        }

        return normalized;
    }

    private static string CreateDestinationKey(MusicSettingKey key) => string.Join(
        "\u001f",
        key.Scope,
        key.ScopeFormKey,
        key.MusicTypeFormKey);

    private static string BuildGeneratedEditorId(string prefix, string value)
    {
        var safe = new string(value
            .Where(character => char.IsLetterOrDigit(character) || character == '_')
            .ToArray());
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "Generated";
        }

        return $"GF_{prefix}_{safe}";
    }

    private sealed class GenerationPluginContext
    {
        public GenerationPluginContext(string pluginFileName, SkyrimMod mod)
        {
            PluginFileName = pluginFileName;
            Mod = mod;
        }

        public string PluginFileName { get; }
        public SkyrimMod Mod { get; }
        public ISet<string> UsedMusicTrackEditorIds { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record WorldSpaceTarget(
        string FormKey,
        string? EditorId,
        string MusicTypeEditorId,
        MusicSettingSource RepresentativeSetting,
        IReadOnlyList<MusicSettingSource> Settings);

}
