using Microsoft.Data.Sqlite;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// One DFG external runtime patch row.  The source/local identity identifies
/// the existing game form; ChangesJson is the sparse DFG field document.
/// </summary>
public sealed record DfgExternalMusicTypePatch(
    string SourcePlugin,
    uint LocalFormId,
    string FormKind,
    string EditorId,
    string WinningPlugin,
    string ChangesJson);

/// <summary>
/// Writes the SQLite package database consumed by Dynamic Forms Generator.
/// DFG creates these tables itself when a package is first saved, but creating
/// the same schema here makes the generated package immediately inspectable and
/// lets the external patch rows ship together with the import queue.
/// </summary>
public sealed class DfgExternalPatchDatabaseWriter
{
    private const string DatabaseSchema = """
        PRAGMA journal_mode=DELETE;
        PRAGMA user_version=1;
        CREATE TABLE IF NOT EXISTS forms (
            editor_id TEXT PRIMARY KEY NOT NULL,
            form_kind TEXT NOT NULL,
            plugin_number INTEGER NOT NULL DEFAULT 0,
            local_id INTEGER NOT NULL DEFAULT 0,
            payload TEXT NOT NULL,
            updated_at INTEGER NOT NULL DEFAULT (unixepoch())
        );
        CREATE TABLE IF NOT EXISTS patches (
            target_editor_id TEXT PRIMARY KEY NOT NULL,
            target_package TEXT NOT NULL,
            form_kind TEXT NOT NULL,
            payload TEXT NOT NULL,
            updated_at INTEGER NOT NULL DEFAULT (unixepoch())
        );
        CREATE TABLE IF NOT EXISTS external_patches (
            source_plugin TEXT NOT NULL COLLATE NOCASE,
            local_form_id INTEGER NOT NULL,
            form_kind TEXT NOT NULL,
            editor_id TEXT NOT NULL DEFAULT '',
            winning_plugin TEXT NOT NULL DEFAULT '',
            changes_json TEXT NOT NULL,
            updated_at INTEGER NOT NULL DEFAULT (unixepoch()),
            PRIMARY KEY(source_plugin, local_form_id, form_kind)
        );
        """;

    public int Write(
        string databasePath,
        IReadOnlyList<DfgExternalMusicTypePatch> patches)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(patches);

        var fullPath = Path.GetFullPath(databasePath);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException($"DFG package.dbの親フォルダを解決できません：{databasePath}");
        }

        Directory.CreateDirectory(parent);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }

        ValidatePatches(patches);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = DatabaseSchema;
            schema.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO external_patches(
                source_plugin,
                local_form_id,
                form_kind,
                editor_id,
                winning_plugin,
                changes_json,
                updated_at)
            VALUES(
                $source_plugin,
                $local_form_id,
                $form_kind,
                $editor_id,
                $winning_plugin,
                $changes_json,
                unixepoch());
            """;

        var sourcePlugin = insert.CreateParameter();
        sourcePlugin.ParameterName = "$source_plugin";
        insert.Parameters.Add(sourcePlugin);
        var localFormId = insert.CreateParameter();
        localFormId.ParameterName = "$local_form_id";
        insert.Parameters.Add(localFormId);
        var formKind = insert.CreateParameter();
        formKind.ParameterName = "$form_kind";
        insert.Parameters.Add(formKind);
        var editorId = insert.CreateParameter();
        editorId.ParameterName = "$editor_id";
        insert.Parameters.Add(editorId);
        var winningPlugin = insert.CreateParameter();
        winningPlugin.ParameterName = "$winning_plugin";
        insert.Parameters.Add(winningPlugin);
        var changesJson = insert.CreateParameter();
        changesJson.ParameterName = "$changes_json";
        insert.Parameters.Add(changesJson);

        foreach (var patch in patches)
        {
            sourcePlugin.Value = patch.SourcePlugin;
            localFormId.Value = patch.LocalFormId;
            formKind.Value = patch.FormKind;
            editorId.Value = patch.EditorId;
            winningPlugin.Value = patch.WinningPlugin;
            changesJson.Value = patch.ChangesJson;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
        return patches.Count;
    }

    private static void ValidatePatches(
        IReadOnlyList<DfgExternalMusicTypePatch> patches)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in patches)
        {
            if (string.IsNullOrWhiteSpace(patch.SourcePlugin) ||
                patch.LocalFormId == 0 ||
                !patch.FormKind.Equals("MusicType", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(patch.EditorId) ||
                string.IsNullOrWhiteSpace(patch.ChangesJson))
            {
                throw new InvalidOperationException(
                    "DFG外部Music Typeパッチに必要な識別情報がありません。");
            }

            var identity = $"{patch.SourcePlugin}\u001f{patch.LocalFormId:X8}\u001f{patch.FormKind}";
            if (!identities.Add(identity))
            {
                throw new InvalidOperationException(
                    $"同じMusic TypeへのDFG外部パッチが重複しています：{patch.SourcePlugin}|{patch.LocalFormId:X}");
            }

            using var document = System.Text.Json.JsonDocument.Parse(patch.ChangesJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.GetInt32() != 1 ||
                !root.TryGetProperty("fields", out var fields) ||
                !fields.TryGetProperty("musicTypeTracks", out var tracks) ||
                !tracks.TryGetProperty("operation", out var operation) ||
                (operation.GetString() is not "replace" and not "set") ||
                !tracks.TryGetProperty("value", out var value) ||
                value.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"DFG外部パッチのMusic Type Track差分が不正です：{patch.EditorId}");
            }
        }
    }
}
