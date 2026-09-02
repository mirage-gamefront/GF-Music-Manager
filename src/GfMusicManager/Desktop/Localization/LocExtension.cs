using System.Windows.Markup;
using GfMusicManager.Core.Localization;

namespace GfMusicManager.Desktop.Localization;

[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension(string key)
    {
        Key = key;
    }

    public string Key { get; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return UiText.Get(Key);
    }
}
