using System.Security.Cryptography;
using System.Text;
using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Localization;
using GfMusicManager.Core.Planning;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Generation;

/// <summary>
/// Moves condition support records owned by source mods into the generated
/// product and remaps conditions to those generated records.  Each supported
/// source record is materialized once in the primary generated plugin so split
/// output plugins share the same dependency.
/// </summary>
internal sealed class MusicConditionDependencyMaterializer
{
    private const string GeneratedFormListPrefix = "GF_FormList";

    private readonly IReadOnlyList<FormListDependency> _formLists;
    private readonly Dictionary<string, string> _generatedFormKeys =
        new(StringComparer.OrdinalIgnoreCase);

    private MusicConditionDependencyMaterializer(
        IReadOnlyList<FormListDependency> formLists)
    {
        _formLists = formLists;
    }

    public int DependencyCount => _formLists.Count;

    public static MusicConditionDependencyMaterializer Create(
        IReadOnlyList<MusicGenerationPlanEntry> adoptedEntries,
        IReadOnlyList<MusicSettingSource> settings)
    {
        ArgumentNullException.ThrowIfNull(adoptedEntries);
        ArgumentNullException.ThrowIfNull(settings);

        var pluginSources = BuildPluginSourceIndex(settings);
        var formListKeys = adoptedEntries
            .SelectMany(entry => entry.Tracks)
            .SelectMany(track => track.Conditions)
            .Select(condition => condition.FormListFormKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => FormKey.Factory(value!))
            .Distinct()
            .Where(formKey => IsSourceModOwned(formKey.ModKey, pluginSources))
            .ToArray();
        var formLists = formListKeys
            .Select(formKey => ReadFormList(formKey, pluginSources))
            .ToArray();
        return new MusicConditionDependencyMaterializer(formLists);
    }

    public void MaterializeInto(SkyrimMod outputMod)
    {
        ArgumentNullException.ThrowIfNull(outputMod);
        foreach (var dependency in _formLists)
        {
            var generated = new FormList(
                outputMod,
                CreateEditorId(dependency.EditorId, dependency.SourceFormKey))
            {
                Items = new()
            };
            foreach (var item in dependency.Items)
            {
                generated.Items.Add(
                    new FormLink<ISkyrimMajorRecordGetter>(item));
            }
            outputMod.FormLists.Add(generated);

            _generatedFormKeys.Add(
                dependency.SourceFormKey.ToString(),
                generated.FormKey.ToString());
        }
    }

    public IReadOnlyList<MusicConditionSource> RemapConditions(
        IEnumerable<MusicConditionSource> conditions)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        return conditions.Select(RemapCondition).ToArray();
    }

    private MusicConditionSource RemapCondition(MusicConditionSource condition)
    {
        if (string.IsNullOrWhiteSpace(condition.FormListFormKey) ||
            !_generatedFormKeys.TryGetValue(
                condition.FormListFormKey,
                out var generatedFormKey))
        {
            return condition;
        }

        return condition with
        {
            FormListFormKey = generatedFormKey,
            DataSummary = ReplaceFormKey(
                condition.DataSummary,
                condition.FormListFormKey,
                generatedFormKey)
        };
    }

    private static string ReplaceFormKey(
        string value,
        string sourceFormKey,
        string generatedFormKey) =>
        string.IsNullOrWhiteSpace(value)
            ? value
            : value.Replace(
                sourceFormKey,
                generatedFormKey,
                StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<ModKey, PluginSource> BuildPluginSourceIndex(
        IReadOnlyList<MusicSettingSource> settings) =>
        settings
            .SelectMany(setting => new[]
                {
                    setting.Record.Plugin,
                    setting.MusicTypeRecord.Plugin
                }
                .Concat(setting.Tracks.Select(track => track.Record.Plugin)))
            .GroupBy(
                plugin => ModKey.FromNameAndExtension(plugin.Name))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(plugin => plugin.ModPriority)
                    .ThenByDescending(plugin => plugin.LoadOrderIndex)
                    .First());

    private static bool IsSourceModOwned(
        ModKey modKey,
        IReadOnlyDictionary<ModKey, PluginSource> pluginSources) =>
        pluginSources.TryGetValue(modKey, out var plugin) &&
        plugin.ModPriority != int.MinValue;

    private static FormListDependency ReadFormList(
        FormKey formKey,
        IReadOnlyDictionary<ModKey, PluginSource> pluginSources)
    {
        if (!pluginSources.TryGetValue(formKey.ModKey, out var plugin) ||
            !File.Exists(plugin.Path))
        {
            throw new MusicGenerationOutputException(
                UiText.Format(
                    "Generation.Output.ConditionDependencyPluginMissing",
                    formKey,
                    plugin?.Path ?? formKey.ModKey.FileName.String));
        }

        using var sourceMod = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(formKey.ModKey, plugin.Path),
            SkyrimRelease.SkyrimSE,
            new BinaryReadParameters());
        var source = sourceMod.FormLists.Records.FirstOrDefault(record =>
            record.FormKey == formKey);
        if (source is null)
        {
            throw new MusicGenerationOutputException(
                UiText.Format(
                    "Generation.Output.ConditionFormListMissing",
                    formKey));
        }

        var items = source.Items
            .Select(item => item.FormKey)
            .ToArray();
        var unsupportedItem = items.FirstOrDefault(item =>
            IsSourceModOwned(item.ModKey, pluginSources));
        if (!unsupportedItem.IsNull)
        {
            throw new MusicGenerationOutputException(
                UiText.Format(
                    "Generation.Output.ConditionFormListNestedDependency",
                    formKey,
                    unsupportedItem));
        }

        return new FormListDependency(
            formKey,
            source.EditorID,
            items);
    }

    private static string CreateEditorId(string? sourceEditorId, FormKey formKey)
    {
        var name = string.IsNullOrWhiteSpace(sourceEditorId)
            ? "Source"
            : new string(sourceEditorId
                .Select(character => char.IsLetterOrDigit(character) ? character : '_')
                .ToArray());
        if (name.Length > 48)
        {
            name = name[..48];
        }

        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(formKey.ToString())))[..8];
        return $"{GeneratedFormListPrefix}_{name}_{hash}";
    }

    private sealed record FormListDependency(
        FormKey SourceFormKey,
        string? EditorId,
        IReadOnlyList<FormKey> Items);
}
