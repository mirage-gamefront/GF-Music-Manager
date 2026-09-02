using GfMusicManager.Core.Analysis;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicAuditReportTests
{
    [Fact]
    public void Build_EmitsExactlyOneRowForEveryAssetAndFlagsDuplicatePaths()
    {
        var assets = new[]
        {
            Asset("Mod A", @"music\a.xwm"),
            Asset("Mod B", @"music\a.xwm"),
            Asset("Mod B", @"music\b.xwm")
        };
        var scan = new ScanResult(
            Profile(),
            Array.Empty<ModSource>(),
            Array.Empty<PluginSource>(),
            Array.Empty<PluginRecordSource>(),
            assets,
            Array.Empty<ScanIssue>());
        var analysis = new MusicAnalysisResult(
            Array.Empty<MusicSettingSource>(),
            new Dictionary<string, IReadOnlyList<MusicSettingSource>>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<ScanIssue>());

        var report = new MusicAuditReportBuilder().Build(scan, analysis);

        Assert.Equal(assets.Length, report.AssetCount);
        Assert.Equal(assets.Length, report.Assets.Count);
        Assert.Equal(assets.Length, report.UnmappedAssetCount);
        Assert.Equal(1, report.DuplicateVirtualPathGroupCount);
        Assert.Equal(2, report.DuplicateVirtualPathRowCount);
        Assert.All(report.Assets, row => Assert.Contains("Unmapped", row.Flags));
        Assert.Equal(assets.Length, report.AssetsByMod.Values.Sum());
    }

    [Fact]
    public void Writer_WritesMachineReadableJsonAndAllRowsToTsv()
    {
        var root = Directory.CreateTempSubdirectory("gf-music-audit-");
        try
        {
            var assets = new[] { Asset("Fixture", @"music\fixture.xwm") };
            var scan = new ScanResult(
                Profile(),
                Array.Empty<ModSource>(),
                Array.Empty<PluginSource>(),
                Array.Empty<PluginRecordSource>(),
                assets,
                Array.Empty<ScanIssue>());
            var analysis = new MusicAnalysisResult(
                Array.Empty<MusicSettingSource>(),
                new Dictionary<string, IReadOnlyList<MusicSettingSource>>(StringComparer.OrdinalIgnoreCase),
                Array.Empty<ScanIssue>());
            var report = new MusicAuditReportBuilder().Build(scan, analysis);

            var files = MusicAuditReportWriter.Write(root.FullName, "audit", report);

            Assert.True(File.Exists(files.JsonPath));
            Assert.True(File.Exists(files.TsvPath));
            Assert.True(File.Exists(files.RecordsTsvPath));
            Assert.True(File.Exists(files.ContextRecordsTsvPath));
            Assert.Contains("\"AssetCount\": 1", File.ReadAllText(files.JsonPath));
            Assert.Equal(2, File.ReadAllLines(files.TsvPath).Length);
            Assert.Single(File.ReadAllLines(files.RecordsTsvPath));
            Assert.Single(File.ReadAllLines(files.ContextRecordsTsvPath));
            Assert.Contains(@"music\fixture.xwm", File.ReadAllText(files.TsvPath));
        }
        finally
        {
            try
            {
                root.Delete(recursive: true);
            }
            catch (IOException)
            {
                // The temporary fixture can be cleaned by the OS later.
            }
        }
    }

    [Fact]
    public void Build_CollectsReferencedContextRecordsSeparately()
    {
        var plugin = new PluginSource(
            "Fixture.esp",
            @"C:\Fixture\Fixture.esp",
            "Fixture",
            @"C:\Fixture",
            true,
            true,
            1,
            1);
        var cell = new PluginRecordSource(
            "000001:Fixture.esp",
            "Cell",
            "RiverwoodExterior01",
            false,
            plugin,
            true)
        {
            References = new[]
            {
                new PluginRecordReferenceSource("Music", "000010:Fixture.esp"),
                new PluginRecordReferenceSource("Location", "000020:Fixture.esp")
            }
        };
        var location = new PluginRecordSource(
            "000020:Fixture.esp",
            "Location",
            "RiverwoodLocation",
            false,
            plugin,
            true)
        {
            DisplayName = "リバーウッド"
        };
        var scan = new ScanResult(
            Profile(),
            Array.Empty<ModSource>(),
            new[] { plugin },
            new[] { cell, location },
            Array.Empty<AssetSource>(),
            Array.Empty<ScanIssue>());
        var analysis = new MusicAnalysisResult(
            Array.Empty<MusicSettingSource>(),
            new Dictionary<string, IReadOnlyList<MusicSettingSource>>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<ScanIssue>());

        var report = new MusicAuditReportBuilder().Build(scan, analysis);

        var context = Assert.Single(report.ContextRecords);
        Assert.Equal("Location", context.RecordType);
        Assert.Equal("000020:Fixture.esp", context.FormKey);
        Assert.Equal("リバーウッド", context.DisplayName);
        Assert.Single(report.Records);
        Assert.Equal("Cell", report.Records[0].RecordType);
    }

    [Fact]
    public void Build_ExportsMusicTrackConditionsWithTheRecordAudit()
    {
        var plugin = new PluginSource(
            "Fixture.esp",
            @"C:\Fixture\Fixture.esp",
            "Fixture",
            @"C:\Fixture",
            true,
            true,
            1,
            1);
        var track = new PluginRecordSource(
            "000001:Fixture.esp",
            "MusicTrack",
            "Track_TownDay",
            false,
            plugin,
            true)
        {
            Conditions = new[]
            {
                new PluginRecordConditionSource(
                    "GetCurrentTime",
                    "GreaterThanOrEqualTo",
                    5f,
                    "OR",
                    "GetCurrentTimeConditionData",
                    string.Empty)
                {
                    RunOnType = "Reference",
                    RunOnTypeIndex = -1,
                    ReferenceFormKey = "000014:Skyrim.esm"
                }
            }
        };
        var scan = new ScanResult(
            Profile(),
            Array.Empty<ModSource>(),
            new[] { plugin },
            new[] { track },
            Array.Empty<AssetSource>(),
            Array.Empty<ScanIssue>());
        var analysis = new MusicAnalysisResult(
            Array.Empty<MusicSettingSource>(),
            new Dictionary<string, IReadOnlyList<MusicSettingSource>>(StringComparer.OrdinalIgnoreCase),
            Array.Empty<ScanIssue>());

        var report = new MusicAuditReportBuilder().Build(scan, analysis);

        var condition = Assert.Single(Assert.Single(report.Records).Conditions);
        Assert.Equal("GetCurrentTime", condition.FunctionName);
        Assert.Equal(5f, condition.ComparisonValue);
        Assert.Equal("OR", condition.Flags);
        Assert.Equal("Reference", condition.RunOnType);
        Assert.Equal("000014:Skyrim.esm", condition.ReferenceFormKey);
    }

    private static AssetSource Asset(string modName, string virtualPath) =>
        new(
            virtualPath,
            AssetSourceKind.Loose,
            modName,
            $@"C:\Fixture\{modName}",
            true,
            $@"C:\Fixture\{modName}\{virtualPath.Replace('\\', Path.DirectorySeparatorChar)}",
            null,
            1);

    private static Mo2ProfileSnapshot Profile() =>
        new(
            @"C:\Fixture\MO2",
            "Main",
            @"C:\Fixture\MO2\profiles\Main",
            Array.Empty<string>(),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            true,
            true,
            true);
}
