namespace GfMusicManager.Core.Analysis;

public static class MusicAudioPathIdentity
{
    public static string NormalizeAssetPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var normalized = path
            .Replace('/', '\\')
            .TrimStart('\\');
        const string dataPrefix = "data\\";
        return normalized.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[dataPrefix.Length..]
            : normalized;
    }

    public static string NormalizeMusicIdentity(string path)
    {
        var normalized = NormalizeAssetPath(path);
        var extension = Path.GetExtension(normalized);
        return string.IsNullOrWhiteSpace(extension)
            ? normalized
            : normalized[..^extension.Length];
    }
}
