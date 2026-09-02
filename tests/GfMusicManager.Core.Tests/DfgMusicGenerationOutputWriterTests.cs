using System.Text.Json;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Planning;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda.Plugins;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class DfgMusicGenerationOutputWriterTests
{
    [Fact]
    public void Generate_WritesDfgTrackAndMusicTypeExternalPatchWithTrackCondition()
    {
        var root = Directory.CreateTempSubdirectory("gfmm-dfg-");
        try
        {
            var asset = CreateAsset(root.FullName, "Music Mod", "fixture.xwm");
            var setting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var track = CreateTrack(
                setting.Record.Plugin,
                "000201:Fixture.esp",
                "Track_Morning",
                asset.VirtualPath) with
            {
                Conditions = new[]
                {
                    MusicConditionSource.CreateCurrentTime(8f, "GreaterThanOrEqualTo")
                }
            };
            setting = setting with { Tracks = new[] { track } };
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(asset, new[] { setting });

            var output = new DfgMusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new DfgMusicGenerationOutputOptions
                {
                    OutputModDirectory = Path.Combine(root.FullName, "DFG Output")
                });

            Assert.Equal(1, output.MusicTrackCount);
            Assert.Equal(1, output.MusicTypeCount);
            Assert.Equal(1, output.ExternalMusicTypePatchCount);
            var importPath = Assert.Single(output.ImportPaths);
            Assert.True(File.Exists(importPath), importPath);
            Assert.True(File.Exists(output.PackageDatabasePath));
            Assert.Equal(
                Path.Combine(
                    output.OutputModDirectory,
                    "Viny Mods",
                    "Dynamic Forms Generator",
                    "Packages",
                    "GF_Music_Manager_DFG"),
                output.PackageDirectory);
            Assert.DoesNotContain(
                Path.Combine("Data", "Viny Mods"),
                output.PackageDirectory,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(Path.Combine(output.OutputModDirectory, "Data")));

            using var trackDocument = JsonDocument.Parse(
                File.ReadAllText(importPath));
            Assert.Equal(
                "MusicTrack",
                trackDocument.RootElement.GetProperty("formKind").GetString());

            var generatedTrackId = trackDocument.RootElement
                .GetProperty("editorId")
                .GetString();
            Assert.StartsWith("GFMM_DFG_MUST_", generatedTrackId, StringComparison.Ordinal);
            Assert.Equal(1, trackDocument.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                "music/fixture.xwm",
                trackDocument.RootElement.GetProperty("musicTrackPath").GetString());
            var condition = Assert.Single(
                trackDocument.RootElement.GetProperty("conditions").EnumerateArray());
            Assert.Equal(18, condition.GetProperty("functionId").GetInt32());
            Assert.Equal(3, condition.GetProperty("opCode").GetInt32());
            Assert.Equal(uint.MaxValue, condition.GetProperty("dataId").GetUInt32());

            var patch = Assert.Single(ReadExternalPatches(output.PackageDatabasePath));
            Assert.Equal("Fixture.esp", patch.SourcePlugin);
            Assert.Equal(0x100, patch.LocalFormId);
            Assert.Equal("MusicType", patch.FormKind);
            Assert.StartsWith("GFMM_DFG_PATCH_MUSC_", patch.EditorId, StringComparison.Ordinal);
            using var changes = JsonDocument.Parse(patch.ChangesJson);
            var musicTypeTracks = changes.RootElement
                .GetProperty("fields")
                .GetProperty("musicTypeTracks");
            Assert.Equal(1, changes.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("replace", musicTypeTracks.GetProperty("operation").GetString());
            var typeTrack = Assert.Single(musicTypeTracks.GetProperty("value").EnumerateArray());
            Assert.Equal(
                generatedTrackId,
                typeTrack.GetProperty("editorID").GetString());
            Assert.False(typeTrack.TryGetProperty("formID", out _));
            Assert.Equal("GF Music Manager DFG", trackDocument.RootElement
                .GetProperty("packageName").GetString());
            Assert.True(File.Exists(output.ManifestPath));
            Assert.True(File.Exists(output.MetadataPath));

            using var manifest = JsonDocument.Parse(File.ReadAllText(output.ManifestPath));
            Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("gf-music-manager-dfg", manifest.RootElement
                .GetProperty("packageId").GetString());
            Assert.Equal(JsonValueKind.Array, manifest.RootElement
                .GetProperty("dependencies").ValueKind);
            Assert.Equal(JsonValueKind.Array, manifest.RootElement
                .GetProperty("pluginDependencies").ValueKind);
            Assert.Contains(
                manifest.RootElement.GetProperty("pluginDependencies").EnumerateArray(),
                dependency => dependency.GetProperty("plugin").GetString() == "Fixture.esp");

            AssertDfgPackageDataIsConsistent(output);

            Assert.Empty(Directory.EnumerateFiles(
                output.OutputModDirectory,
                "*.esp",
                SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(
                output.OutputModDirectory,
                "*.xwm",
                SearchOption.AllDirectories));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_KeepVanillaReferencesOfficialTrackButNotOldModTrack()
    {
        var root = Directory.CreateTempSubdirectory("gfmm-dfg-official-");
        try
        {
            var asset = CreateAsset(root.FullName, "Music Mod", "fixture.xwm");
            var officialPlugin = CreatePlugin(root.FullName, "Skyrim.esm", 0, 0);
            var oldModPlugin = CreatePlugin(root.FullName, "Fantasy Music.esp", 10, 10);
            var officialTrack = CreateTrack(
                officialPlugin,
                "000001:Skyrim.esm",
                "MUSCombatOfficial",
                asset.VirtualPath);
            var oldModTrack = CreateTrack(
                oldModPlugin,
                "000001:Fantasy Music.esp",
                "MUSCombatOldMod",
                asset.VirtualPath);
            var typeRecord = new PluginRecordSource(
                "000100:Skyrim.esm",
                "MusicType",
                "MUSCombat",
                false,
                officialPlugin,
                true);
            var setting = new MusicSettingSource(
                MusicSettingScope.MusicType,
                typeRecord.FormKey,
                typeRecord.EditorId,
                typeRecord.FormKey,
                typeRecord.EditorId,
                typeRecord,
                typeRecord,
                new[] { officialTrack, oldModTrack });
            var plan = new MusicGenerationPlan { KeepVanillaMusic = true };
            plan.GetOrCreate(asset, new[] { setting });

            var output = new DfgMusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new DfgMusicGenerationOutputOptions
                {
                    OutputModDirectory = Path.Combine(root.FullName, "DFG Output")
                });

            Assert.Equal(1, output.OfficialReferenceCount);
            Assert.Equal(1, output.ExternalMusicTypePatchCount);
            Assert.Equal(output.MusicTrackCount, output.ImportPaths.Count);
            Assert.NotEmpty(output.ImportPaths);
            foreach (var importPath in output.ImportPaths)
            {
                using var importDocument = JsonDocument.Parse(File.ReadAllText(importPath));
                Assert.Equal(
                    "MusicTrack",
                    importDocument.RootElement.GetProperty("formKind").GetString());
            }
            var patch = Assert.Single(ReadExternalPatches(output.PackageDatabasePath));
            using var document = JsonDocument.Parse(patch.ChangesJson);
            var references = document.RootElement
                .GetProperty("fields")
                .GetProperty("musicTypeTracks")
                .GetProperty("value")
                .EnumerateArray()
                .ToArray();
            Assert.Contains(references, reference =>
                reference.TryGetProperty("formID", out var formId) &&
                formId.GetString() == "Skyrim.esm|1");
            Assert.DoesNotContain(references, reference =>
                reference.TryGetProperty("formID", out var formId) &&
                formId.GetString() == "Fantasy Music.esp|1");
            AssertDfgPackageDataIsConsistent(output);
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Validator_AcceptsPackageAfterDfgConsumesImportQueue()
    {
        var root = Directory.CreateTempSubdirectory("gfmm-dfg-consumed-");
        try
        {
            var asset = CreateAsset(root.FullName, "Music Mod", "fixture.xwm");
            var setting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(asset, new[] { setting });

            var output = new DfgMusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new DfgMusicGenerationOutputOptions
                {
                    OutputModDirectory = Path.Combine(root.FullName, "DFG Output")
                });

            var importPath = Assert.Single(output.ImportPaths);
            using var importDocument = JsonDocument.Parse(File.ReadAllText(importPath));
            var importRoot = importDocument.RootElement;
            var editorId = importRoot.GetProperty("editorId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(editorId));

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = output.PackageDatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO forms(editor_id, form_kind, payload)
                    VALUES($editor_id, $form_kind, $payload);
                    """;
                command.Parameters.AddWithValue("$editor_id", editorId!);
                command.Parameters.AddWithValue("$form_kind", "MusicTrack");
                command.Parameters.AddWithValue("$payload", importDocument.RootElement.GetRawText());
                command.ExecuteNonQuery();
            }

            File.Delete(importPath);

            var validation = new DfgMusicGenerationPackageValidator().Validate(output);

            Assert.True(validation.IsSuccess, string.Join(Environment.NewLine, validation.Errors));
            Assert.Contains(
                validation.Checks,
                check => check.Contains("package.db/forms", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Validator_RejectsExternalPatchReferenceToMissingImportedTrack()
    {
        var root = Directory.CreateTempSubdirectory("gfmm-dfg-invalid-");
        try
        {
            var asset = CreateAsset(root.FullName, "Music Mod", "fixture.xwm");
            var setting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(asset, new[] { setting });

            var output = new DfgMusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new DfgMusicGenerationOutputOptions
                {
                    OutputModDirectory = Path.Combine(root.FullName, "DFG Output")
                });

            using var importedDocument = JsonDocument.Parse(
                File.ReadAllText(Assert.Single(output.ImportPaths)));
            var importedTrackId = importedDocument.RootElement
                .GetProperty("editorId")
                .GetString();
            Assert.False(string.IsNullOrWhiteSpace(importedTrackId));
            var patch = Assert.Single(ReadExternalPatches(output.PackageDatabasePath));
            var invalidChanges = patch.ChangesJson.Replace(
                importedTrackId!,
                "GFMM_DFG_MUST_MISSING",
                StringComparison.Ordinal);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = output.PackageDatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString();
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE external_patches
                    SET changes_json = $changes_json;
                    """;
                command.Parameters.AddWithValue("$changes_json", invalidChanges);
                command.ExecuteNonQuery();
            }

            var validation = new DfgMusicGenerationPackageValidator().Validate(output);
            Assert.False(validation.IsSuccess);
            Assert.Contains(
                validation.Errors,
                error => error.Contains("未生成のMusic Track", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_ReportsNonMusicTypeAssignmentsWithoutWritingAssignmentRecords()
    {
        var root = Directory.CreateTempSubdirectory("gfmm-dfg-assignment-");
        try
        {
            var asset = CreateAsset(root.FullName, "Music Mod", "fixture.xwm");
            var setting = CreateScopedSetting(
                root.FullName,
                MusicSettingScope.Cell,
                "Cell_Fixture",
                "MUSExploreFixture",
                "Fixture.esp",
                "000100:Fixture.esp",
                "000200:Fixture.esp");
            var track = CreateTrack(
                setting.Record.Plugin,
                "000201:Fixture.esp",
                "Track_Fixture",
                asset.VirtualPath);
            setting = setting with { Tracks = new[] { track } };
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(asset, new[] { setting });

            var output = new DfgMusicGenerationOutputWriter().Generate(
                plan,
                new[] { setting },
                new DfgMusicGenerationOutputOptions
                {
                    OutputModDirectory = Path.Combine(root.FullName, "DFG Output")
                });

            Assert.Equal(1, output.UnsupportedAssignmentCount);
            Assert.True(File.Exists(output.PackageDatabasePath));
            using var metadata = JsonDocument.Parse(File.ReadAllText(output.MetadataPath));
            var unsupported = Assert.Single(
                metadata.RootElement.GetProperty("unsupportedAssignments").EnumerateArray());
            Assert.Equal("Cell", unsupported.GetProperty("scope").GetString());
            Assert.Equal(
                "000200:Fixture.esp",
                unsupported.GetProperty("scopeFormKey").GetString());
            Assert.Empty(
                Directory.EnumerateFiles(output.OutputModDirectory, "*.esp", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    [Fact]
    public void Generate_RequiresOverwriteForAnExistingOutput()
    {
        var root = Directory.CreateTempSubdirectory("gfmm-dfg-overwrite-");
        try
        {
            var asset = CreateAsset(root.FullName, "Music Mod", "fixture.xwm");
            var setting = CreateMusicTypeSetting(root.FullName, "MUSExploreFixture");
            var plan = new MusicGenerationPlan { KeepVanillaMusic = false };
            plan.GetOrCreate(asset, new[] { setting });
            var outputDirectory = Path.Combine(root.FullName, "DFG Output");
            var writer = new DfgMusicGenerationOutputWriter();
            writer.Generate(
                plan,
                new[] { setting },
                new DfgMusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                });

            Assert.Throws<DfgMusicGenerationOutputException>(() => writer.Generate(
                plan,
                new[] { setting },
                new DfgMusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory
                }));

            var overwritten = writer.Generate(
                plan,
                new[] { setting },
                new DfgMusicGenerationOutputOptions
                {
                    OutputModDirectory = outputDirectory,
                    OverwriteExisting = true
                });
            Assert.True(File.Exists(overwritten.MetadataPath));
        }
        finally
        {
            DeleteDirectory(root.FullName);
        }
    }

    private static AssetSource CreateAsset(string root, string modName, string fileName)
    {
        var sourcePath = Path.Combine(root, modName, "music", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllBytes(sourcePath, [1, 2, 3]);
        return new AssetSource(
            $"music/{fileName}",
            AssetSourceKind.Loose,
            modName,
            Path.Combine(root, modName),
            true,
            sourcePath,
            null,
            3);
    }

    private static MusicTrackSource CreateTrack(
        PluginSource plugin,
        string formKey,
        string editorId,
        string virtualPath)
    {
        var record = new PluginRecordSource(
            formKey,
            "MusicTrack",
            editorId,
            false,
            plugin,
            true);
        return new MusicTrackSource(
            formKey,
            editorId,
            new[] { virtualPath },
            record);
    }

    private static MusicSettingSource CreateMusicTypeSetting(
        string root,
        string editorId)
    {
        var plugin = CreatePlugin(root, "Fixture.esp", 1, 1);
        var record = new PluginRecordSource(
            "000100:Fixture.esp",
            "MusicType",
            editorId,
            false,
            plugin,
            true);
        return new MusicSettingSource(
            MusicSettingScope.MusicType,
            record.FormKey,
            editorId,
            record.FormKey,
            editorId,
            record,
            record,
            Array.Empty<MusicTrackSource>());
    }

    private static MusicSettingSource CreateScopedSetting(
        string root,
        MusicSettingScope scope,
        string scopeEditorId,
        string musicTypeEditorId,
        string pluginName,
        string musicTypeFormKey,
        string scopeFormKey)
    {
        var plugin = CreatePlugin(root, pluginName, 1, 1);
        var musicTypeRecord = new PluginRecordSource(
            musicTypeFormKey,
            "MusicType",
            musicTypeEditorId,
            false,
            plugin,
            true);
        var scopeRecord = musicTypeRecord with
        {
            FormKey = scopeFormKey,
            RecordType = scope.ToString(),
            EditorId = scopeEditorId
        };
        return new MusicSettingSource(
            scope,
            scopeFormKey,
            scopeEditorId,
            musicTypeFormKey,
            musicTypeEditorId,
            scopeRecord,
            musicTypeRecord,
            Array.Empty<MusicTrackSource>());
    }

    private static PluginSource CreatePlugin(
        string root,
        string name,
        int loadOrderIndex,
        int priority) =>
        new(
            name,
            Path.Combine(root, name),
            Path.GetFileNameWithoutExtension(name),
            root,
            true,
            true,
            loadOrderIndex,
            priority);

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private static IReadOnlyList<ExternalPatchRow> ReadExternalPatches(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_plugin, local_form_id, form_kind, editor_id,
                   winning_plugin, changes_json
            FROM external_patches
            ORDER BY source_plugin, local_form_id, form_kind;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<ExternalPatchRow>();
        while (reader.Read())
        {
            result.Add(new ExternalPatchRow(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }

        return result;
    }

    private static void AssertDfgPackageDataIsConsistent(
        DfgMusicGenerationOutputResult output)
    {
        Assert.Equal(
            output.MusicTrackCount,
            output.ImportPaths.Count);

        var importedTrackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var importPath in output.ImportPaths)
        {
            Assert.True(File.Exists(importPath), importPath);
            Assert.Equal(".json", Path.GetExtension(importPath));
            using var document = JsonDocument.Parse(File.ReadAllText(importPath));
            var root = document.RootElement;
            Assert.Equal("MusicTrack", root.GetProperty("formKind").GetString());
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            var editorId = root.GetProperty("editorId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(editorId));
            Assert.True(importedTrackIds.Add(editorId!), editorId);
            Assert.False(string.IsNullOrWhiteSpace(
                root.GetProperty("musicTrackPath").GetString()));
            Assert.Equal(JsonValueKind.Array, root.GetProperty("conditions").ValueKind);
        }

        var externalPatches = ReadExternalPatches(output.PackageDatabasePath);
        Assert.Equal(output.MusicTypeCount, externalPatches.Count);
        Assert.Equal(output.ExternalMusicTypePatchCount, externalPatches.Count);
        Assert.NotEmpty(ReadPackageTables(output.PackageDatabasePath));

        var referencedTrackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in externalPatches)
        {
            Assert.False(string.IsNullOrWhiteSpace(patch.SourcePlugin));
            Assert.NotEqual(0, patch.LocalFormId);
            Assert.Equal("MusicType", patch.FormKind);
            Assert.StartsWith("GFMM_DFG_PATCH_MUSC_", patch.EditorId, StringComparison.Ordinal);

            using var changes = JsonDocument.Parse(patch.ChangesJson);
            var root = changes.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            var field = root.GetProperty("fields").GetProperty("musicTypeTracks");
            Assert.Equal("replace", field.GetProperty("operation").GetString());
            Assert.Equal(JsonValueKind.Array, field.GetProperty("value").ValueKind);
            foreach (var reference in field.GetProperty("value").EnumerateArray())
            {
                var editorId = reference.GetProperty("editorID").GetString();
                Assert.False(string.IsNullOrWhiteSpace(editorId));
                referencedTrackIds.Add(editorId!);
                if (importedTrackIds.Contains(editorId!))
                {
                    Assert.False(reference.TryGetProperty("formID", out _), editorId);
                }
                else
                {
                    Assert.True(reference.TryGetProperty("formID", out var formId));
                    Assert.False(string.IsNullOrWhiteSpace(formId.GetString()));
                }
            }
        }

        Assert.Subset(referencedTrackIds, importedTrackIds);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(output.PackageDirectory, "*.json", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Equals("MusicType.json", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlySet<string> ReadPackageTables(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = command.ExecuteReader();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Contains("forms", tables);
        Assert.Contains("patches", tables);
        Assert.Contains("external_patches", tables);
        return tables;
    }

    private sealed record ExternalPatchRow(
        string SourcePlugin,
        long LocalFormId,
        string FormKind,
        string EditorId,
        string WinningPlugin,
        string ChangesJson);
}
