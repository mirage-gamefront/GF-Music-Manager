using System.Reflection;

namespace GfMusicManager.Desktop;

internal static class DesktopProductInfo
{
    private const string ProductName = "GF Music Manager";

    public static string Version { get; } = ResolveVersion();

    public static string WindowTitle => $"{ProductName} {Version}";

    private static string ResolveVersion()
    {
        var informationalVersion = typeof(DesktopProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return typeof(DesktopProductInfo).Assembly.GetName().Version?.ToString()
                ?? "Unknown";
        }

        var buildMetadataSeparator = informationalVersion.IndexOf('+');
        return buildMetadataSeparator >= 0
            ? informationalVersion[..buildMetadataSeparator]
            : informationalVersion;
    }
}
