using System.Text;

namespace GfMusicManager.Core.Generation;

internal static class GeneratedMusicTrackEditorId
{
    private const string FallbackName = "MusicTrack";

    public static string Create(string virtualPath, ISet<string> usedEditorIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);
        ArgumentNullException.ThrowIfNull(usedEditorIds);

        var fileName = Path.GetFileNameWithoutExtension(
            virtualPath.Replace('\\', Path.DirectorySeparatorChar));
        var editorIdBase = Sanitize(fileName);
        if (string.IsNullOrWhiteSpace(editorIdBase))
        {
            editorIdBase = FallbackName;
        }

        var editorId = editorIdBase;
        var suffix = 2;
        while (!usedEditorIds.Add(editorId))
        {
            editorId = $"{editorIdBase}_{suffix++}";
        }

        return editorId;
    }

    private static string Sanitize(string value)
    {
        var result = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                result.Append(character);
                previousWasSeparator = false;
                continue;
            }

            if (previousWasSeparator || result.Length == 0)
            {
                continue;
            }

            result.Append('_');
            previousWasSeparator = true;
        }

        return result.ToString().TrimEnd('_');
    }
}
