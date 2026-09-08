using SkyrimScan.Core.Models;
using GfMusicManager.Core.Localization;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// Adds source-level provenance to errors that are known to belong to one
/// audio source or generated Music Track. Plan-wide and settings-wide errors
/// must not use this formatter.
/// </summary>
internal static class MusicGenerationFailureContext
{
    public static string Append(
        string message,
        AssetSource asset,
        string? trackKey = null,
        string? virtualPath = null) =>
        Append(
            message,
            asset.ModName,
            string.IsNullOrWhiteSpace(trackKey)
                ? Array.Empty<string>()
                : new[] { trackKey },
            virtualPath ?? asset.VirtualPath);

    public static string Append(
        string message,
        string? modName,
        IEnumerable<string>? trackKeys,
        string? virtualPath)
    {
        var tracks = (trackKeys ?? Array.Empty<string>())
            .Where(trackKey => !string.IsNullOrWhiteSpace(trackKey))
            .Select(FormatTrackIdentifier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var trackText = tracks.Length == 0
            ? UiText.Get("Generation.FailureContext.Unknown")
            : string.Join(", ", tracks);

        return UiText.Format(
            "Generation.FailureContext",
            message,
            FormatValue(modName),
            trackText,
            FormatValue(virtualPath));
    }

    private static string FormatTrackIdentifier(string trackKey)
    {
        var parts = trackKey.Split('\u001f');
        if (parts.Length >= 2 &&
            (parts[0].Equals("EditorID", StringComparison.OrdinalIgnoreCase) ||
             parts[0].Equals("FormKey", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(parts[1]))
        {
            return parts[1];
        }

        return trackKey
            .Replace('\u001f', '|')
            .Replace('\u001e', ',');
    }

    private static string FormatValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? UiText.Get("Generation.FailureContext.Unknown")
            : value;
}
