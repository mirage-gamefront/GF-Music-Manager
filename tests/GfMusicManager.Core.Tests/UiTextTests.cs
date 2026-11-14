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

    [Theory]
    [InlineData("ja-JP", UiLanguage.Japanese)]
    [InlineData("ja", UiLanguage.Japanese)]
    [InlineData("en-US", UiLanguage.English)]
    [InlineData("fr-FR", UiLanguage.English)]
    public void FromCultureSuggestsJapaneseOnlyForJapaneseSystems(string culture, string expected)
    {
        Assert.Equal(
            expected,
            UiLanguage.FromCulture(System.Globalization.CultureInfo.GetCultureInfo(culture)));
        Assert.Equal(UiLanguage.English, UiLanguage.FromCulture(null));
    }

    [Fact]
    public void UnknownKeyFailsInsteadOfLeakingAPlaceholderIntoTheUi()
    {
        Assert.Throws<KeyNotFoundException>(() => UiText.Get("Missing.Key"));
    }
}
