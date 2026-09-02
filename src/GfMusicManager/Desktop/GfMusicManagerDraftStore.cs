using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Diagnostics;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;

namespace GfMusicManager.Desktop;

/// <summary>
/// 編集途中の生成計画を、MO2ルートとプロファイルごとに保持する下書きです。
/// 生成済みGFMusicProduct.jsonとは別に管理し、生成成功時に削除します。
/// </summary>
public sealed record GfMusicManagerDraft(
    int SchemaVersion,
    DateTimeOffset SavedAtUtc,
    string Mo2Root,
    string ProfileName,
    bool? KeepVanillaMusic,
    bool CreateWorldSpaceMusicSettings,
    bool DisableSourceEsp,
    IReadOnlyList<GfMusicManagerDraftEntry> Entries);

public sealed record GfMusicManagerDraftEntry(
    string AssetKey,
    bool IsAdopted,
    IReadOnlyList<MusicSettingKey> DestinationKeys,
    IReadOnlyList<MusicConditionSource> Conditions)
{
    public IReadOnlyList<GfMusicManagerDraftTrack> Tracks { get; init; } =
        Array.Empty<GfMusicManagerDraftTrack>();
}

public sealed record GfMusicManagerDraftTrack(
    string TrackKey,
    IReadOnlyList<MusicConditionSource> Conditions);

/// <summary>
/// Profile-scoped automatic draft persistence.  Writes are atomic so an interrupted
/// save does not replace a valid draft with a partial JSON document.
/// </summary>
public sealed class GfMusicManagerDraftStore
{
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _baseDirectory;

    public GfMusicManagerDraftStore(string? baseDirectory = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GF Music Manager",
                "drafts")
            : Path.GetFullPath(baseDirectory);
    }

    public string GetDraftPath(string mo2Root, string profileName)
    {
        var identity = BuildProfileIdentity(mo2Root, profileName);
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
        return Path.Combine(_baseDirectory, $"{hash}.json");
    }

    public GfMusicManagerDraft? Load(string mo2Root, string profileName)
    {
        var path = GetDraftPath(mo2Root, profileName);
        GfMusicManagerLog.Info($"Draft.Load: path={path}.");
        try
        {
            if (!File.Exists(path))
            {
                GfMusicManagerLog.Info("Draft.Load: file does not exist.");
                return null;
            }

            var draft = JsonSerializer.Deserialize<GfMusicManagerDraft>(
                File.ReadAllText(path),
                JsonOptions);
            if (draft is null || draft.SchemaVersion > CurrentSchemaVersion)
            {
                GfMusicManagerLog.Warning(
                    $"Draft.Load: unsupported or empty draft. " +
                    $"schema={draft?.SchemaVersion.ToString() ?? "<null>"}.");
                return null;
            }

            var normalized = draft with
            {
                Mo2Root = string.IsNullOrWhiteSpace(draft.Mo2Root) ? mo2Root : draft.Mo2Root,
                ProfileName = string.IsNullOrWhiteSpace(draft.ProfileName)
                    ? profileName
                    : draft.ProfileName,
                Entries = (draft.Entries ?? Array.Empty<GfMusicManagerDraftEntry>())
                    .Select(NormalizeEntry)
                    .ToArray()
            };
            GfMusicManagerLog.Info(
                $"Draft.Load: complete. schema={normalized.SchemaVersion}, " +
                $"entries={normalized.Entries.Count}, " +
                $"tracks={normalized.Entries.Sum(entry => entry.Tracks.Count)}, " +
                $"savedAt={normalized.SavedAtUtc:O}.");
            return normalized;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            GfMusicManagerLog.Exception("Draft.Load failed; ignoring draft", exception);
            return null;
        }
    }

    public bool Save(GfMusicManagerDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var path = GetDraftPath(draft.Mo2Root, draft.ProfileName);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        GfMusicManagerLog.Info(
            $"Draft.Save: path={path}, entries={draft.Entries.Count}, " +
            $"tracks={draft.Entries.Sum(entry => entry.Tracks.Count)}, " +
            $"keepVanilla={draft.KeepVanillaMusic?.ToString() ?? "unset"}, " +
            $"worldSpace={draft.CreateWorldSpaceMusicSettings}, " +
            $"disableSourceEsp={draft.DisableSourceEsp}.");
        try
        {
            Directory.CreateDirectory(_baseDirectory);
            var normalized = draft with
            {
                SchemaVersion = CurrentSchemaVersion,
                SavedAtUtc = draft.SavedAtUtc == default ? DateTimeOffset.UtcNow : draft.SavedAtUtc,
                Entries = (draft.Entries ?? Array.Empty<GfMusicManagerDraftEntry>())
                    .Select(NormalizeEntry)
                    .ToArray()
            };
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
            GfMusicManagerLog.Info("Draft.Save: complete.");
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            GfMusicManagerLog.Exception("Draft.Save failed; continuing without persisted draft", exception);
            TryDeleteTemporaryFile(temporaryPath);
            return false;
        }
    }

    public bool Delete(string mo2Root, string profileName)
    {
        var path = GetDraftPath(mo2Root, profileName);
        GfMusicManagerLog.Info($"Draft.Delete: path={path}.");
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            File.Delete(path);
            GfMusicManagerLog.Info("Draft.Delete: complete.");
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            GfMusicManagerLog.Exception("Draft.Delete failed", exception);
            return false;
        }
    }

    private static string BuildProfileIdentity(string mo2Root, string profileName)
    {
        if (string.IsNullOrWhiteSpace(mo2Root))
        {
            throw new ArgumentException(UiText.Get("Draft.Mo2RootRequired"), nameof(mo2Root));
        }

        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new ArgumentException(UiText.Get("Draft.ProfileRequired"), nameof(profileName));
        }

        return $"{Path.GetFullPath(mo2Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}\n" +
               profileName.Trim();
    }

    private static GfMusicManagerDraftEntry NormalizeEntry(GfMusicManagerDraftEntry entry) =>
        entry with
        {
            DestinationKeys = entry.DestinationKeys ?? Array.Empty<MusicSettingKey>(),
            Conditions = entry.Conditions ?? Array.Empty<MusicConditionSource>(),
            Tracks = (entry.Tracks ?? Array.Empty<GfMusicManagerDraftTrack>())
                .Where(track => !string.IsNullOrWhiteSpace(track.TrackKey))
                .Select(track => track with
                {
                    Conditions = track.Conditions ?? Array.Empty<MusicConditionSource>()
                })
                .ToArray()
        };

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The primary save error is already logged; a stale temp file is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // The primary save error is already logged; a stale temp file is harmless.
        }
    }
}
