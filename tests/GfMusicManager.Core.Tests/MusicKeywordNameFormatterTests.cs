using GfMusicManager.Core.Analysis;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicKeywordNameFormatterTests
{
    [Theory]
    [InlineData("ActorTypeDragon", "ドラゴン")]
    [InlineData("ActorTypeUndead", "アンデッド")]
    [InlineData("ActorTypeDraugr", "ドラウグル")]
    [InlineData("DLCDwarvenPuzzleDungeonIsOrchestrator", "ドワーフ謎解きダンジョンの統括者")]
    public void InferJapaneseName_TranslatesGenericEditorIdTokens(
        string editorId,
        string expected)
    {
        Assert.Equal(expected, MusicKeywordNameFormatter.InferJapaneseName(editorId));
    }

    [Theory]
    [InlineData("SoulCairnAurora", "ソウル・ケルン・オーロラ")]
    [InlineData("SovngardeClear", "ソブンガルデ・晴天")]
    [InlineData("USKP_SkyhavenTempleEntranceOvercastRainRE", "スカイ・ヘブン聖堂入口・曇天・雨")]
    public void InferWeatherJapaneseName_ExplainsKnownWeatherEditorIds(
        string editorId,
        string expected)
    {
        Assert.Equal(expected, MusicWeatherNameFormatter.InferJapaneseName(editorId));
    }

    [Fact]
    public void InferJapaneseName_ReturnsNullWhenAnUnknownTokenWouldBeGuessed()
    {
        Assert.Null(MusicKeywordNameFormatter.InferJapaneseName("ActorTypeModSpecificThing"));
    }
}
