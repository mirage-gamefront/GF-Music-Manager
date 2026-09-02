using System.Security.Cryptography;
using System.Text;

namespace GfMusicManager.Application;

public sealed class MusicDraftApplicationService
{
    private readonly string _baseDirectory;

    public MusicDraftApplicationService(string? baseDirectory = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GF Music Manager",
                "drafts")
            : Path.GetFullPath(baseDirectory);
    }

    public string GetProfileDraftPath(string mo2Root, string profileName)
    {
        if (string.IsNullOrWhiteSpace(mo2Root))
        {
            throw new ArgumentException("MO2ルートが空です。", nameof(mo2Root));
        }

        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ArgumentException("プロファイル名が空です。", nameof(profileName));
        }

        var identity =
            $"{Path.GetFullPath(mo2Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}\n" +
            profileName.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return Path.Combine(_baseDirectory, $"{hash}.json");
    }

    public bool Reset(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return false;
        }

        File.Delete(fullPath);
        return true;
    }

    public bool ResetProfile(string mo2Root, string profileName) =>
        Reset(GetProfileDraftPath(mo2Root, profileName));
}

