using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicScanProgressFormatterTests
{
    [Fact]
    public void FormatsConflictStagesAsAnIndependentCheck()
    {
        UiText.SetLanguage(UiLanguage.Japanese);
        try
        {
            var text = MusicScanProgressFormatter.Format(new ScanProgress(
                ScanIssueSeverity.Info,
                "ConflictCompare",
                "raw message must not be displayed",
                12,
                100));

            Assert.Equal("競合チェック：音源を比較しています…", text);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }

    [Fact]
    public void FormatsMusicAnalysisWithoutCallingItAPluginScan()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var text = MusicScanProgressFormatter.Format(new ScanProgress(
                ScanIssueSeverity.Info,
                "MusicAnalysis",
                "日本語の下層メッセージ",
                0,
                1));

            Assert.Equal("Analyzing music definitions…", text);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }

    [Fact]
    public void FormatsResultPreparationUsingTheUserFacingMessage()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var text = MusicScanProgressFormatter.Format(new ScanProgress(
                ScanIssueSeverity.Info,
                "ResultPrepare",
                "日本語の下層メッセージ",
                16,
                760));

            Assert.Equal("Creating the audio list…", text);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }

    [Fact]
    public void FormatsResultPlanUsingTheUserFacingMessage()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var text = MusicScanProgressFormatter.Format(new ScanProgress(
                ScanIssueSeverity.Info,
                "ResultPlan",
                "日本語の下層メッセージ",
                16,
                760));

            Assert.Equal("Applying audio and definitions to the generation plan…", text);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }

    [Theory]
    [InlineData("ConflictRead")]
    [InlineData("ConflictFingerprint")]
    [InlineData("ConflictCompare")]
    [InlineData("ConflictFinalize")]
    [InlineData("MusicAnalysis")]
    [InlineData("ResultPlan")]
    [InlineData("ResultPrepare")]
    [InlineData("ResultRestore")]
    [InlineData("ResultFinalize")]
    public void EnglishFormattingDoesNotExposeJapaneseProducerMessages(string stage)
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var text = MusicScanProgressFormatter.Format(new ScanProgress(
                ScanIssueSeverity.Info,
                stage,
                "日本語が表示されてはいけません",
                1,
                2));

            Assert.DoesNotMatch("[ぁ-んァ-ヶ一-龠々ー]", text);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }
}
