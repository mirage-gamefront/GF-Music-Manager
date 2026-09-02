using System.Text.Json;
using System.Text.Json.Serialization;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Planning;

namespace GfMusicManager.Application;

/// <summary>
/// Machine-readable snapshot of user-owned generation decisions.
/// AssetKey and MusicSettingKey are references into a scan result; source
/// records are not copied into the plan document.
/// </summary>
public sealed record MusicPlanSnapshot(
    bool? KeepVanillaMusic,
    IReadOnlyList<MusicPlanEntrySnapshot> Entries);

public sealed record MusicPlanEntrySnapshot(
    string AssetKey,
    bool IsAdopted,
    IReadOnlyList<MusicSettingKey> DestinationKeys,
    IReadOnlyList<MusicPlanTrackSnapshot> Tracks,
    IReadOnlyList<MusicConditionSource> LegacyConditions);

public sealed record MusicPlanTrackSnapshot(
    string TrackKey,
    IReadOnlyList<MusicConditionSource> Conditions);

public sealed record MusicPlanDocument(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    MusicPlanSnapshot Plan)
{
    public const int CurrentSchemaVersion = 1;

    public static MusicPlanDocument Create(MusicPlanSnapshot plan) =>
        new(CurrentSchemaVersion, DateTimeOffset.UtcNow, plan);
}

public sealed record MusicPlanApplyResult(
    int RestoredEntryCount,
    int MissingEntryCount,
    IReadOnlyList<string> MissingAssetKeys);

public sealed record MusicPlanPreparationResult(
    MusicGenerationPlan Plan,
    MusicAssetBindingIndex AssetBindings);

public static class MusicPlanJson
{
    public static JsonSerializerOptions CreateOptions(bool writeIndented = true) => new()
    {
        WriteIndented = writeIndented,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public static void Save(string path, MusicPlanSnapshot plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(plan);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = MusicPlanDocument.Create(plan);
        var json = JsonSerializer.Serialize(document, CreateOptions());
        File.WriteAllText(fullPath, json);
    }

    public static MusicPlanDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var document = JsonSerializer.Deserialize<MusicPlanDocument>(
            File.ReadAllText(fullPath),
            CreateOptions(writeIndented: false));
        if (document is null)
        {
            throw new InvalidDataException($"Plan is empty or invalid: {fullPath}");
        }

        if (document.SchemaVersion != MusicPlanDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported plan schema version: {document.SchemaVersion}");
        }

        return document;
    }
}
