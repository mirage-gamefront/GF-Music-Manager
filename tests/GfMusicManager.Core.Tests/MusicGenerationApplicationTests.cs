using GfMusicManager.Application;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicGenerationApplicationTests
{
    [Fact]
    public void Generate_RestoresSavedPlanAndRunsPostGenerationDiagnostic()
    {
        var root = Directory.CreateTempSubdirectory("gfmm-application-generation-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Fixture Music", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [1, 2, 3, 4]);

            var asset = new AssetSource(
                @"music\fixture.xwm",
                AssetSourceKind.Loose,
                "Fixture Music",
                Path.Combine(root.FullName, "Fixture Music"),
                true,
                assetPath,
                null,
                4);
            var plugin = new PluginSource(
                "Fixture.esp",
                Path.Combine(root.FullName, "Fixture.esp"),
                "Fixture",
                root.FullName,
                true,
                true,
                1,
                1);
            var musicTypeRecord = new PluginRecordSource(
                "000100:Fixture.esp",
                "MusicType",
                "MUSExploreFixture",
                false,
                plugin);
            var trackRecord = new PluginRecordSource(
                "000200:Fixture.esp",
                "MusicTrack",
                "TrackFixture",
                false,
                plugin);
            var track = new MusicTrackSource(
                trackRecord.FormKey,
                trackRecord.EditorId,
                new[] { asset.VirtualPath },
                trackRecord)
            {
                Conditions = new[]
                {
                    MusicConditionSource.CreateCurrentTime(8f, "GreaterThanOrEqualTo")
                }
            };
            var setting = new MusicSettingSource(
                MusicSettingScope.MusicType,
                musicTypeRecord.FormKey,
                musicTypeRecord.EditorId,
                musicTypeRecord.FormKey,
                musicTypeRecord.EditorId,
                musicTypeRecord,
                musicTypeRecord,
                new[] { track });

            var scan = CreateScanResult(root.FullName, asset);
            var analysis = new MusicAnalysisResult(
                new[] { setting },
                new Dictionary<string, IReadOnlyList<MusicSettingSource>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [asset.VirtualPath] = new[] { setting }
                },
                Array.Empty<ScanIssue>());
            var scanResult = new MusicScanApplicationResult(
                new MusicScanRequest { Mo2Root = root.FullName },
                scan,
                analysis,
                CreateEmptyDuplicates());

            var planService = new MusicPlanApplicationService();
            var plan = planService.CreatePlan(scanResult);
            plan.KeepVanillaMusic = false;
            var snapshot = planService.Capture(plan);
            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var progressReports = new List<MusicGenerationProgress>();

            var generated = new MusicGenerationApplicationService().Generate(
                scanResult,
                snapshot,
                new MusicGenerationApplicationOptions
                {
                    OutputModDirectory = outputDirectory,
                    ExistingMtdFileNames = Array.Empty<string>(),
                    Progress = new RecordingGenerationProgress(progressReports)
                });

            Assert.Equal(1, generated.PlanApplication.RestoredEntryCount);
            Assert.True(generated.Output.Diagnostic.IsSuccess, generated.Output.Diagnostic.Details);
            Assert.Single(generated.Output.Tracks);
            Assert.True(File.Exists(generated.Output.MtdFilePath));
            Assert.True(File.Exists(generated.Output.ManifestPath));
            Assert.True(File.Exists(Path.Combine(outputDirectory, "GF Music Product.esp")));
            Assert.Equal(
                new[]
                {
                    MusicGenerationProgressStage.Preparing,
                    MusicGenerationProgressStage.Resolving,
                    MusicGenerationProgressStage.Validating,
                    MusicGenerationProgressStage.Writing,
                    MusicGenerationProgressStage.Diagnosing,
                    MusicGenerationProgressStage.Completed
                },
                progressReports
                    .Select(report => report.Stage)
                    .Distinct()
                    .ToArray());
            Assert.Contains(
                progressReports,
                report => report.Stage == MusicGenerationProgressStage.Preparing &&
                          report.Current == 1 &&
                          report.Total == 1);

            var directOutputDirectory = Path.Combine(root.FullName, "GF Music Product Direct");
            var scanWithoutAssets = scanResult with
            {
                Scan = scanResult.Scan with { Assets = Array.Empty<AssetSource>() }
            };
            var directGenerated = new MusicGenerationApplicationService().Generate(
                scanWithoutAssets,
                plan,
                new MusicGenerationApplicationOptions
                {
                    OutputModDirectory = directOutputDirectory,
                    ExistingMtdFileNames = Array.Empty<string>()
                });

            Assert.Single(directGenerated.Output.Tracks);
            Assert.Equal(plan.Entries.Count, directGenerated.PlanApplication.RestoredEntryCount);
            Assert.True(directGenerated.Output.Diagnostic.IsSuccess);

            var outputValidation = new MusicGenerationApplicationService().ValidateOutput(
                scanResult,
                snapshot,
                new MusicGenerationApplicationOptions
                {
                    OutputModDirectory = outputDirectory,
                    ExistingMtdFileNames = Array.Empty<string>()
                });

            Assert.True(
                outputValidation.IsValid,
                outputValidation.Diagnostic.Details);
            Assert.Equal(
                generated.Output.Diagnostic.CheckCount,
                outputValidation.Diagnostic.CheckCount);

            var directPlanOutputDirectory = Path.Combine(
                root.FullName,
                "GF Music Product - Existing Plan");
            var generatedFromExistingPlan = new MusicGenerationApplicationService().Generate(
                scanResult,
                plan,
                new MusicGenerationApplicationOptions
                {
                    OutputModDirectory = directPlanOutputDirectory,
                    ExistingMtdFileNames = Array.Empty<string>()
                });

            Assert.Equal(plan.Entries.Count, generatedFromExistingPlan.PlanApplication.RestoredEntryCount);
            Assert.True(
                generatedFromExistingPlan.Output.Diagnostic.IsSuccess,
                generatedFromExistingPlan.Output.Diagnostic.Details);
            Assert.True(File.Exists(generatedFromExistingPlan.Output.ManifestPath));

            File.Delete(Path.Combine(outputDirectory, "GF Music Product.esp"));
            var invalidOutput = new MusicGenerationApplicationService().ValidateOutput(
                scanResult,
                snapshot,
                new MusicGenerationApplicationOptions
                {
                    OutputModDirectory = outputDirectory,
                    ExistingMtdFileNames = Array.Empty<string>()
                });

            Assert.False(invalidOutput.IsValid);
            Assert.Contains(
                invalidOutput.Diagnostic.Errors,
                error => error.Contains("ESP GF Music Product.esp", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Validate_RejectsPlanThatDoesNotMatchCurrentScan()
    {
        var root = Directory.CreateTempSubdirectory("gfmm-application-validation-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Fixture Music", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [1, 2, 3, 4]);
            var asset = new AssetSource(
                @"music\fixture.xwm",
                AssetSourceKind.Loose,
                "Fixture Music",
                Path.Combine(root.FullName, "Fixture Music"),
                true,
                assetPath,
                null,
                4);
            var scanResult = CreateScanApplicationResultWithoutSettings(root.FullName, asset);
            var invalidSnapshot = new MusicPlanSnapshot(
                false,
                new[]
                {
                    new MusicPlanEntrySnapshot(
                        "missing-asset-key",
                        true,
                        Array.Empty<MusicSettingKey>(),
                        Array.Empty<MusicPlanTrackSnapshot>(),
                        Array.Empty<MusicConditionSource>())
                });

            var validation = new MusicGenerationApplicationService().Validate(
                scanResult,
                invalidSnapshot,
                new MusicGenerationApplicationOptions
                {
                    OutputModDirectory = Path.Combine(root.FullName, "GF Music Product"),
                    ExistingMtdFileNames = Array.Empty<string>()
                });

            Assert.False(validation.IsValid);
            Assert.Contains(
                validation.Errors,
                error => error.Contains("生成計画と現在のスキャン結果が一致しません", StringComparison.Ordinal));
            Assert.False(Directory.Exists(Path.Combine(root.FullName, "GF Music Product")));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void PlanEdit_ChangesRequestedAdoptionAndVanillaPolicy()
    {
        var first = new MusicPlanEntrySnapshot(
            "asset-a",
            true,
            Array.Empty<MusicSettingKey>(),
            Array.Empty<MusicPlanTrackSnapshot>(),
            Array.Empty<MusicConditionSource>());
        var second = first with { AssetKey = "asset-b", IsAdopted = false };
        var snapshot = new MusicPlanSnapshot(true, new[] { first, second });

        var edited = new MusicPlanApplicationService().Edit(
            snapshot,
            new[] { "asset-b" },
            new[] { "asset-a" },
            keepVanillaMusic: false);

        Assert.False(edited.KeepVanillaMusic);
        Assert.False(edited.Entries.Single(entry => entry.AssetKey == "asset-a").IsAdopted);
        Assert.True(edited.Entries.Single(entry => entry.AssetKey == "asset-b").IsAdopted);
    }

    [Fact]
    public void PlanEdit_RejectsUnknownAssetKey()
    {
        var snapshot = new MusicPlanSnapshot(
            false,
            new[]
            {
                new MusicPlanEntrySnapshot(
                    "asset-a",
                    true,
                    Array.Empty<MusicSettingKey>(),
                    Array.Empty<MusicPlanTrackSnapshot>(),
                    Array.Empty<MusicConditionSource>())
            });

        var exception = Assert.Throws<ArgumentException>(() =>
            new MusicPlanApplicationService().Edit(
                snapshot,
                new[] { "missing" },
                Array.Empty<string>()));

        Assert.Contains("計画に存在しない音源キー", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportingAndDraftServices_WriteAndResetMachineReadableFiles()
    {
        var root = Directory.CreateTempSubdirectory("gfmm-application-report-");
        try
        {
            var assetPath = Path.Combine(root.FullName, "Fixture Music", "music", "fixture.xwm");
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
            File.WriteAllBytes(assetPath, [1, 2, 3, 4]);
            var asset = new AssetSource(
                @"music\fixture.xwm",
                AssetSourceKind.Loose,
                "Fixture Music",
                Path.Combine(root.FullName, "Fixture Music"),
                true,
                assetPath,
                null,
                4);
            var scanResult = CreateScanApplicationResultWithoutSettings(root.FullName, asset);
            var reportDirectory = Path.Combine(root.FullName, "report");

            var reportResult = new MusicReportingApplicationService().Write(
                scanResult,
                reportDirectory,
                "fixture");
            var files = reportResult.Files;

            Assert.True(File.Exists(files.JsonPath));
            Assert.True(File.Exists(files.TsvPath));
            Assert.True(File.Exists(files.RecordsTsvPath));
            Assert.True(File.Exists(files.ContextRecordsTsvPath));
            Assert.Contains("AssetCount", File.ReadAllText(files.JsonPath), StringComparison.Ordinal);

            var draftDirectory = Path.Combine(root.FullName, "drafts");
            var draftService = new MusicDraftApplicationService(draftDirectory);
            var draftPath = draftService.GetProfileDraftPath(root.FullName, "Fixture");
            Directory.CreateDirectory(draftDirectory);
            File.WriteAllText(draftPath, "{}", System.Text.Encoding.UTF8);
            Assert.True(draftService.ResetProfile(root.FullName, "Fixture"));
            Assert.False(File.Exists(draftPath));
            Assert.False(draftService.ResetProfile(root.FullName, "Fixture"));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    private static ScanResult CreateScanResult(string root, AssetSource asset) =>
        new(
            CreateProfile(root),
            Array.Empty<ModSource>(),
            Array.Empty<PluginSource>(),
            Array.Empty<PluginRecordSource>(),
            new[] { asset },
            Array.Empty<ScanIssue>());

    private static MusicScanApplicationResult CreateScanApplicationResultWithoutSettings(
        string root,
        AssetSource asset)
    {
        var scan = CreateScanResult(root, asset);
        var analysis = new MusicAnalysisResult(
            Array.Empty<MusicSettingSource>(),
            new Dictionary<string, IReadOnlyList<MusicSettingSource>>(
                StringComparer.OrdinalIgnoreCase),
            Array.Empty<ScanIssue>());
        return new MusicScanApplicationResult(
            new MusicScanRequest { Mo2Root = root },
            scan,
            analysis,
            CreateEmptyDuplicates());
    }

    private static Mo2ProfileSnapshot CreateProfile(string root) =>
        new(
            root,
            "Fixture",
            Path.Combine(root, "profiles", "Fixture"),
            Array.Empty<string>(),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            false,
            false,
            false);

    private static AudioDuplicateAnalysisResult CreateEmptyDuplicates() =>
        new(Array.Empty<AudioDuplicateGroup>(), 0, 0, 0, false);

    private sealed class RecordingGenerationProgress(
        ICollection<MusicGenerationProgress> reports) : IProgress<MusicGenerationProgress>
    {
        public void Report(MusicGenerationProgress value) => reports.Add(value);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
