using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Localization;
using GfMusicManager.Desktop;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Desktop.Tests;

public sealed class GenerationCompletedMessageTests
{
    [Fact]
    public void BuildGenerationCompletedMessage_SeparatesAudioTracksAndHandlingCounts()
    {
        UiText.SetLanguage(UiLanguage.Japanese);
        var result = new MusicGenerationOutputResult(
            @"C:\Output",
            @"C:\Output\product.ini",
            @"C:\Output\manifest.json",
            new[] { new GeneratedPluginOutput("GF Music Product.esp", 3, 0, 0) },
            new[]
            {
                Track("primary-a", "track-a-1"),
                Track("primary-a", "track-a-2"),
                Track("primary-b", "track-b-1", @"music\finale.xwm")
            },
            Array.Empty<GeneratedWorldSpaceOutput>(),
            Array.Empty<GeneratedCellOutput>(),
            null,
            new[]
            {
                Asset("primary-a", false),
                Asset("primary-b", true),
                Asset("finale", true)
            },
            Array.Empty<string>())
        {
            Diagnostic = new MusicGenerationDiagnosticResult(
                true,
                Array.Empty<string>(),
                Array.Empty<string>())
        };

        var message = MainWindow.BuildGenerationCompletedMessage(
            result,
            generatedModEnabled: true,
            profileName: "Fixture");

        Assert.Contains("通常音源：2件", message, StringComparison.Ordinal);
        Assert.Contains("フィナーレ音源：1件", message, StringComparison.Ordinal);
        Assert.Contains("Music Track：3件", message, StringComparison.Ordinal);
        Assert.Contains("参照：1件", message, StringComparison.Ordinal);
        Assert.Contains("コピー：2件", message, StringComparison.Ordinal);
        Assert.Equal(3, message.Split("────────────────────", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("音源：3件", message, StringComparison.Ordinal);
    }

    private static GeneratedMusicTrackOutput Track(
        string assetKey,
        string trackKey,
        string? finaleVirtualPath = null) =>
        new(
            assetKey,
            $@"music\{trackKey}.xwm",
            trackKey,
            "000001:GF Music Product.esp",
            "GF Music Product.esp",
            Array.Empty<string>(),
            Array.Empty<GfMusicManager.Core.Analysis.MusicConditionSource>())
        {
            TrackKey = trackKey,
            FinaleVirtualPath = finaleVirtualPath
        };

    private static GeneratedAssetOutput Asset(string assetKey, bool copied) =>
        new(
            assetKey,
            $@"music\{assetKey}.xwm",
            AssetSourceKind.Loose,
            $@"C:\Source\{assetKey}.xwm",
            null,
            copied ? $@"C:\Output\music\{assetKey}.xwm" : null,
            100,
            copied);
}
