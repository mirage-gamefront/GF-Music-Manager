using GfMusicManager.Core.Localization;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class UiTextTests
{
    [Fact]
    public void EverySupportedLanguageHasTheSameKeys()
    {
        Assert.Empty(UiText.ValidateCatalogs());
    }

    [Theory]
    [InlineData(UiLanguage.Japanese, "表示言語")]
    [InlineData(UiLanguage.English, "Display language")]
    public void SetLanguageSelectsTheRequestedCatalog(string language, string expected)
    {
        try
        {
            UiText.SetLanguage(language);

            Assert.Equal(expected, UiText.Get("Settings.Language.Label"));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.Japanese);
        }
    }

    [Fact]
    public void UnknownKeyFailsInsteadOfLeakingAPlaceholderIntoTheUi()
    {
        Assert.Throws<KeyNotFoundException>(() => UiText.Get("Missing.Key"));
    }
}
