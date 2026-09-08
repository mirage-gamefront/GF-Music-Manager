using GfMusicManager.Core.Localization;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// Keeps arbitrary exception text from leaking into an English user-facing
/// generation message when a lower layer returns localized diagnostics.
/// </summary>
public static class GenerationMessageFormatter
{
    public static string ExceptionDetail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = exception.Message;
        return UiText.Language.Equals(UiLanguage.English, StringComparison.OrdinalIgnoreCase) &&
               message.Any(IsJapaneseCharacter)
            ? UiText.Get("Generation.ExceptionDetailsInLog")
            : message;
    }

    private static bool IsJapaneseCharacter(char value) =>
        value is >= '\u3040' and <= '\u30ff' or
        >= '\u3400' and <= '\u4dbf' or
        >= '\u4e00' and <= '\u9fff' or
        >= '\uf900' and <= '\ufaff';
}
