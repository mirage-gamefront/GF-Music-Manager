using GfMusicManager.Application;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Plugins;
using Xunit;

namespace GfMusicManager.Core.Tests;

/// <summary>
/// GetStageDone is written with its stage number, and a Track whose playback
/// condition still cannot be written is left out and reported instead of
/// aborting the whole generation.
/// </summary>
public sealed class MusicGenerationUnsupportedConditionTests
{
    private const string QuestFormKey = "000337:Skyrim.esm";

    [Fact]
    public void Generate_WritesGetStageDoneWithQuestAndStage()
    {
        var root = Directory.CreateTempSubdirectory("gfmm-stage-done-");
        try
        {
            var asset = CreateAsset(root.FullName, "Beyond Reach", "stage.xwm");
            var setting = CreateSetting(root.FullName);
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(asset, new[] { setting }, new[] { CreateStageDone(40) });

            var outputDirectory = Path.Combine(root.FullName, "GF Music Product");
            var result = new MusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new MusicGenerationOutputOptions { OutputModDirectory = outputDirectory });

            Assert.True(result.Diagnostic.IsSuccess, result.Diagnostic.Details);
            var records = new PluginRecordScanner().Read(new PluginSource(
                "GF Music Product.esp",
                Path.Combine(outputDirectory, "GF Music Product.esp"),
                "GF Music Product",
                outputDirectory,
                true,
                true,
                0,
                0));
            var written = MusicConditionSource.From(Assert.Single(
                Assert.Single(records.Where(record => record.RecordType == "MusicTrack"))
                    .Conditions));
            Assert.Equal("GetStageDone", written.FunctionName);
            Assert.Equal(QuestFormKey, written.QuestFormKey);
            Assert.Equal(40, written.StageIndex);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void ConditionKey_SeparatesStagesOfTheSameQuest()
    {
        Assert.NotEqual(
            MusicConditionFormatter.CreateRecordKey(CreateStageDone(40)),
            MusicConditionFormatter.CreateRecordKey(CreateStageDone(50)));
        Assert.Equal(
            MusicConditionFormatter.CreateRecordKey(CreateStageDone(40)),
            MusicConditionFormatter.CreateRecordKey(CreateStageDone(40)));
    }

    [Fact]
    public void GetUnsupportedReason_ReportsStageDoneWithoutStageAndOtherFunctions()
    {
        Assert.Null(MusicGenerationConditionSupport.GetUnsupportedReason(CreateStageDone(10)));
        Assert.Null(MusicGenerationConditionSupport.GetUnsupportedReason(
            MusicConditionSource.CreateCurrentTime(8f, "GreaterThanOrEqualTo")));

        var missingStage = CreateStageDone(10) with { StageIndex = null };
        Assert.Contains(
            "GetStageDone",
            MusicGenerationConditionSupport.GetUnsupportedReason(missingStage));
        Assert.Contains(
            "GetGlobalValue",
            MusicGenerationConditionSupport.GetUnsupportedReason(CreateGlobalValue()));
    }

    [Fact]
    public void Filter_LeavesOutOnlyTheUnsupportedTrackAndKeepsTheUserPlan()
    {
        var root = Directory.CreateTempSubdirectory("gfmm-filter-");
        try
        {
            var supported = CreateAsset(root.FullName, "Supported Mod", "supported.xwm");
            var unsupported = CreateAsset(root.FullName, "Beyond Reach", "unsupported.xwm");
            var setting = CreateSetting(root.FullName);
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(supported, new[] { setting }, new[] { CreateStageDone(40) });
            plan.GetOrCreate(unsupported, new[] { setting }, new[] { CreateGlobalValue() });

            var result = MusicGenerationConditionSupport.Filter(plan);

            var skipped = Assert.Single(result.SkippedTracks);
            Assert.Equal("Beyond Reach", skipped.ModName);
            Assert.Contains("GetGlobalValue", skipped.Message);
            Assert.Contains("Beyond Reach", skipped.Message);
            Assert.Equal(
                new[] { supported.VirtualPath },
                result.Plan.Entries
                    .Where(entry => entry.IsAdopted)
                    .Select(entry => entry.Asset!.VirtualPath)
                    .ToArray());
            Assert.Equal(2, plan.Entries.Count(entry => entry.IsAdopted));
            Assert.All(plan.Entries, entry => Assert.Single(entry.Tracks));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_SkipsUnsupportedTracksAndReportsThemInsteadOfAborting()
    {
        var root = Directory.CreateTempSubdirectory("gfmm-skip-generation-");
        try
        {
            var supportedAsset = CreateAsset(root.FullName, "Supported Mod", "supported.xwm");
            var stageAsset = CreateAsset(root.FullName, "Beyond Reach", "stage.xwm");
            var unsupportedAsset = CreateAsset(root.FullName, "Other Mod", "unsupported.xwm");
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
            var tracks = new[]
            {
                CreateTrack(plugin, "000200:Fixture.esp", "TrackSupported", supportedAsset,
                    MusicConditionSource.CreateCurrentTime(8f, "GreaterThanOrEqualTo")),
                CreateTrack(plugin, "000201:Fixture.esp", "TrackStage", stageAsset,
                    CreateStageDone(40)),
                CreateTrack(plugin, "000202:Fixture.esp", "TrackUnsupported", unsupportedAsset,
                    CreateGlobalValue())
            };
            var setting = new MusicSettingSource(
                MusicSettingScope.MusicType,
                musicTypeRecord.FormKey,
                musicTypeRecord.EditorId,
                musicTypeRecord.FormKey,
                musicTypeRecord.EditorId,
                musicTypeRecord,
                musicTypeRecord,
                tracks);
            var assets = new[] { supportedAsset, stageAsset, unsupportedAsset };
            var scanResult = new MusicScanApplicationResult(
                new MusicScanRequest { Mo2Root = root.FullName },
                new ScanResult(
                    CreateProfile(root.FullName),
                    Array.Empty<ModSource>(),
                    Array.Empty<PluginSource>(),
                    Array.Empty<PluginRecordSource>(),
                    assets,
                    Array.Empty<ScanIssue>()),
                new MusicAnalysisResult(
                    new[] { setting },
                    assets.ToDictionary(
                        asset => asset.VirtualPath,
                        asset => (IReadOnlyList<MusicSettingSource>)new[] { setting },
                        StringComparer.OrdinalIgnoreCase),
                    Array.Empty<ScanIssue>()),
                new AudioDuplicateAnalysisResult(
                    Array.Empty<AudioDuplicateGroup>(), 0, 0, 0, false));

            var planService = new MusicPlanApplicationService();
            var plan = planService.CreatePlan(scanResult);
            plan.KeepVanillaMusic = false;
            var options = new MusicGenerationApplicationOptions
            {
                OutputModDirectory = Path.Combine(root.FullName, "GF Music Product"),
                ExistingMtdFileNames = Array.Empty<string>()
            };
            var service = new MusicGenerationApplicationService();

            var validation = service.Validate(scanResult, planService.Capture(plan), options);
            Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
            Assert.Single(validation.SkippedTracks);
            Assert.True(validation.Warnings.Count >= 2);

            var generated = service.Generate(scanResult, plan, options);

            Assert.True(generated.Output.Diagnostic.IsSuccess, generated.Output.Diagnostic.Details);
            var skipped = Assert.Single(generated.SkippedTracks);
            Assert.Equal("Other Mod", skipped.ModName);
            Assert.Contains("GetGlobalValue", skipped.Message);
            Assert.Equal(2, generated.Output.Tracks.Count);
            Assert.DoesNotContain(
                generated.Output.Tracks,
                track => track.VirtualPath.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3, plan.Entries.Count(entry => entry.IsAdopted));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    private static MusicConditionSource CreateStageDone(int stage) =>
        new(
            "GetStageDone",
            "EqualTo",
            1f,
            string.Empty,
            "GetStageDoneConditionData",
            $"Quest={QuestFormKey}, Stage={stage}")
        {
            QuestFormKey = QuestFormKey,
            StageIndex = stage
        };

    private static MusicConditionSource CreateGlobalValue() =>
        new(
            "GetGlobalValue",
            "EqualTo",
            1f,
            string.Empty,
            "GetGlobalValueConditionData",
            "Global=000999:Skyrim.esm");

    private static MusicTrackSource CreateTrack(
        PluginSource plugin,
        string formKey,
        string editorId,
        AssetSource asset,
        MusicConditionSource condition)
    {
        var record = new PluginRecordSource(formKey, "MusicTrack", editorId, false, plugin);
        return new MusicTrackSource(
            record.FormKey,
            record.EditorId,
            new[] { asset.VirtualPath },
            record)
        {
            Conditions = new[] { condition }
        };
    }

    private static AssetSource CreateAsset(string root, string modName, string fileName)
    {
        var path = Path.Combine(root, modName, "music", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return new AssetSource(
            $@"music\{fileName}",
            AssetSourceKind.Loose,
            modName,
            Path.Combine(root, modName),
            true,
            path,
            null,
            4);
    }

    private static MusicSettingSource CreateSetting(string root)
    {
        var plugin = new PluginSource(
            "Fixture.esp",
            Path.Combine(root, "Fixture.esp"),
            "Fixture",
            root,
            true,
            true,
            1,
            1);
        var record = new PluginRecordSource(
            "000100:Fixture.esp",
            "MusicType",
            "MUSStageFixture",
            false,
            plugin);
        return new MusicSettingSource(
            MusicSettingScope.MusicType,
            record.FormKey,
            record.EditorId,
            record.FormKey,
            record.EditorId,
            record,
            record,
            Array.Empty<MusicTrackSource>());
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

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
