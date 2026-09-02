using GfMusicManager.Core.Analysis;
using SkyrimScan.Core.Models;
using SkyrimScan.Core.Scanning;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class RealMo2MusicAuditTests
{
    [Fact]
    [Trait("Category", "RealMo2Audit")]
    public void ActualMo2Profile_IsAuditedAsACompleteAssetList()
    {
        var mo2Root = Environment.GetEnvironmentVariable("GF_MUSIC_AUDIT_MO2_ROOT");
        if (string.IsNullOrWhiteSpace(mo2Root) || !Directory.Exists(mo2Root))
        {
            Console.WriteLine(
                "REAL_MO2_AUDIT_NOT_RUN: GF_MUSIC_AUDIT_MO2_ROOT was not provided.");
            return;
        }

        var profileName = Environment.GetEnvironmentVariable("GF_MUSIC_AUDIT_PROFILE");
        var includeDisabled = bool.TryParse(
            Environment.GetEnvironmentVariable("GF_MUSIC_AUDIT_INCLUDE_DISABLED"),
            out var parsedIncludeDisabled) && parsedIncludeDisabled;
        var outputDirectory = Environment.GetEnvironmentVariable("GF_MUSIC_AUDIT_OUTPUT")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "reports", "gf-music-manager-audit");
        var expectedAssetCount = Environment.GetEnvironmentVariable("GF_MUSIC_AUDIT_EXPECTED_ASSETS");
        var generatedProductDirectory = Path.Combine(
            Path.GetFullPath(mo2Root),
            "mods",
            "GF Music Product");
        var excludedModNames = Directory.Exists(generatedProductDirectory)
            ? new HashSet<string>(new[] { "GF Music Product" }, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var scan = new Mo2Scanner().Scan(new ScanOptions
        {
            Mo2Root = mo2Root,
            ProfileName = profileName,
            IncludeDisabledMods = includeDisabled,
            ReadPluginRecords = true,
            ScanArchives = true,
            ScanLooseAssets = true,
            ExcludedModNames = excludedModNames
        });
        var analysis = new MusicSettingsAnalyzer().Analyze(scan);
        var combatKeywordConditions = analysis.ConditionCandidates
            .Where(condition => condition.FunctionName == "GetCombatTargetHasKeyword")
            .ToArray();
        Assert.NotEmpty(combatKeywordConditions);
        Assert.All(combatKeywordConditions, condition =>
        {
            Assert.False(string.IsNullOrWhiteSpace(condition.KeywordFormKey));
            Assert.False(string.IsNullOrWhiteSpace(condition.KeywordEditorId));
            Assert.False(string.IsNullOrWhiteSpace(condition.KeywordJapaneseExplanation));
            Assert.Equal("EditorIDの一般語から自動補足", condition.KeywordExplanationSource);
            Assert.False(string.IsNullOrWhiteSpace(condition.KeywordDefinitionPluginName));
            Assert.DoesNotContain("FormLinkOrIndex", condition.DataSummary, StringComparison.Ordinal);
        });
        Assert.Contains(
            "（ドラゴン）",
            MusicConditionFormatter.Format(
                combatKeywordConditions.First(condition =>
                    condition.KeywordEditorId == "ActorTypeDragon")));
        var report = new MusicAuditReportBuilder().Build(
            scan,
            analysis,
            includeDisabledMods: includeDisabled);
        var files = MusicAuditReportWriter.Write(
            outputDirectory,
            "full-audit",
            report);

        Console.WriteLine($"AUDIT_JSON={files.JsonPath}");
        Console.WriteLine($"AUDIT_TSV={files.TsvPath}");
        Console.WriteLine($"AUDIT_RECORDS_TSV={files.RecordsTsvPath}");
        Console.WriteLine($"AUDIT_CONTEXT_RECORDS_TSV={files.ContextRecordsTsvPath}");
        Console.WriteLine(
            $"AUDIT_SUMMARY assets={report.AssetCount} mapped={report.MappedAssetCount} " +
            $"unmapped={report.UnmappedAssetCount} settings={report.MusicSettingCount} " +
            $"scanIssues={report.ScanIssueCount} analysisIssues={report.MusicAnalysisIssueCount} " +
            $"duplicatePaths={report.DuplicateVirtualPathGroupCount} " +
            $"longPlacement={report.LongPlacementCount} " +
            $"repeatedLabels={report.RepeatedPlacementLabelCount} " +
            $"contextRecords={report.ContextRecords.Count} " +
            $"excludedMods={string.Join(',', excludedModNames)}");

        Assert.NotEmpty(report.Assets);
        Assert.Equal(report.AssetCount, report.Assets.Count);
        Assert.Equal(
            report.AssetCount,
            report.Assets
                .Select(row => $"{row.ModName}\\u001f{row.VirtualPath}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
        Assert.Equal(report.AssetCount, report.AssetsByMod.Values.Sum());
        Assert.Equal(report.AssetCount, report.AssetsBySourceKind.Values.Sum());
        Assert.Equal(
            report.AssetCount,
            report.MappedAssetCount + report.UnmappedAssetCount);
        Assert.Equal(0, report.LongPlacementCount);
        Assert.Equal(0, report.RepeatedPlacementLabelCount);

        if (int.TryParse(expectedAssetCount, out var expected))
        {
            Assert.Equal(expected, report.AssetCount);
        }
    }
}
