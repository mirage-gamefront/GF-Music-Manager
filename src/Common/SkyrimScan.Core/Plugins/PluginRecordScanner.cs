using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using SkyrimScan.Core.Models;
using System.Collections;
using System.Globalization;
using System.Reflection;

namespace SkyrimScan.Core.Plugins;

public sealed class PluginRecordScanner
{
    public IReadOnlyList<PluginRecordSource> Read(
        PluginSource pluginSource,
        CancellationToken cancellationToken = default,
        ICollection<ScanIssue>? issues = null,
        IReadOnlySet<string>? includedRecordTypes = null,
        bool retainOnlyMusicAssignments = false)
    {
        ArgumentNullException.ThrowIfNull(pluginSource);
        cancellationToken.ThrowIfCancellationRequested();

        var modKey = ModKey.FromNameAndExtension(pluginSource.Name);
        var readParameters = new BinaryReadParameters
        {
            StringsParam = new StringsReadParameters
            {
                TargetLanguage = Language.Japanese
            }
        };
        using var plugin = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(modKey, pluginSource.Path),
            SkyrimRelease.SkyrimSE,
            readParameters);

        var records = new List<PluginRecordSource>();
        Exception? displayNameFailure = null;
        foreach (var record in plugin.EnumerateMajorRecords())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recordType = ReadRecordType(record);
            if (includedRecordTypes is not null && !includedRecordTypes.Contains(recordType))
            {
                continue;
            }

            var metadata = ReadMetadata(record);
            if (retainOnlyMusicAssignments &&
                IsScopeRecord(recordType) &&
                !metadata.References.Any(reference =>
                    reference.FieldName is "Music" or "Sounds.Music"))
            {
                continue;
            }

            var displayName = ReadDisplayName(record, out var recordDisplayNameFailure);
            displayNameFailure ??= recordDisplayNameFailure;
            var source = new PluginRecordSource(
                record.FormKey.ToString(),
                recordType,
                ReadEditorId(record),
                record.IsDeleted,
                pluginSource)
            {
                DisplayName = displayName
            };
            records.Add(source with
            {
                References = metadata.References,
                Assets = metadata.Assets,
                Conditions = metadata.Conditions
            });
        }

        if (displayNameFailure is not null)
        {
            issues?.Add(new ScanIssue(
                ScanIssueSeverity.Warning,
                "Plugin",
                pluginSource.Path,
                "Localized record names could not be read; record structure was retained without some display names.",
                displayNameFailure.ToString()));
        }

        return records;
    }

    private static bool IsScopeRecord(string recordType) =>
        recordType.Equals("Cell", StringComparison.OrdinalIgnoreCase) ||
        recordType.Equals("Location", StringComparison.OrdinalIgnoreCase) ||
        recordType.Equals("Region", StringComparison.OrdinalIgnoreCase) ||
        recordType.Equals("Worldspace", StringComparison.OrdinalIgnoreCase);

    private static string? ReadDisplayName(
        IMajorRecordGetter record,
        out Exception? failure)
    {
        failure = null;
        try
        {
            if (record is ITranslatedNamedGetter { Name: { } name })
            {
                return CleanDisplayName(name.String);
            }

            if (record is IRegionGetter { Map: ITranslatedNamedGetter { Name: { } mapName } })
            {
                return CleanDisplayName(mapName.String);
            }

            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception;
            return null;
        }
    }

    private static string? CleanDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith("$", StringComparison.Ordinal))
        {
            return null;
        }

        return value.Trim();
    }

    private static PluginRecordMetadataSource ReadMetadata(IMajorRecordGetter record)
    {
        var references = new List<PluginRecordReferenceSource>();
        var assets = new List<PluginRecordAssetSource>();
        var conditions = new List<PluginRecordConditionSource>();

        switch (record)
        {
            case ICellGetter cell:
                AddReference(references, "Music", cell.Music);
                AddReference(references, "Location", cell.Location);
                AddReference(references, "Regions", cell.Regions);

                break;
            case ILocationGetter location:
                AddReference(references, "Music", location.Music);
                AddReference(references, "ParentLocation", location.ParentLocation);
                break;
            case IRegionGetter region:
                AddReference(references, "Worldspace", region.Worldspace);
                if (region.Sounds is not null)
                {
                    AddReference(references, "Sounds.Music", region.Sounds.Music);
                }

                break;
            case IWorldspaceGetter worldspace:
                AddReference(references, "Music", worldspace.Music);
                AddReference(references, "Location", worldspace.Location);
                break;
            case IMusicTypeGetter musicType:
                AddReference(references, "Tracks", musicType.Tracks);
                break;
            case IMusicTrackGetter musicTrack:
                AddReference(references, "Tracks", musicTrack.Tracks);
                AddAsset(assets, "TrackFilename", musicTrack.TrackFilename);
                AddAsset(assets, "FinaleFilename", musicTrack.FinaleFilename);
                AddConditions(conditions, musicTrack);
                break;
        }

        return new PluginRecordMetadataSource(references, assets, conditions);
    }

    private static void AddConditions(
        ICollection<PluginRecordConditionSource> conditions,
        IMusicTrackGetter musicTrack)
    {
        var property = musicTrack.GetType().GetProperty(
            "Conditions",
            BindingFlags.Instance | BindingFlags.Public);
        if (property?.GetValue(musicTrack) is not IEnumerable values)
        {
            return;
        }

        foreach (var value in values)
        {
            if (value is null)
            {
                continue;
            }

            var data = GetPropertyValue(value, "Data");
            var dataType = data?.GetType().Name ?? "UnknownConditionData";
            var functionName = dataType.EndsWith("ConditionData", StringComparison.Ordinal)
                ? dataType[..^"ConditionData".Length]
                : dataType;
            var comparisonValueObject = GetPropertyValue(value, "ComparisonValue");
            var comparisonValue = ConvertToSingle(comparisonValueObject);
            var compareOperator = GetPropertyValue(value, "CompareOperator")?.ToString() ??
                                  "Unknown";
            var flags = GetPropertyValue(value, "Flags")?.ToString() ?? string.Empty;
            var comparisonValueType = value is IConditionGlobalGetter
                ? "Global"
                : value is IConditionFloatGetter
                    ? "Float"
                    : value.GetType().Name;

            var condition = new PluginRecordConditionSource(
                functionName,
                compareOperator,
                comparisonValue,
                flags,
                dataType,
                ReadDataSummary(data))
            {
                KeywordFormKey = ReadKeywordFormKey(data),
                WeatherFormKey = ReadWeatherFormKey(data),
                ComparisonValueType = comparisonValueType,
                ComparisonGlobalFormKey = comparisonValueType.Equals(
                    "Global",
                    StringComparison.OrdinalIgnoreCase)
                    ? ReadFormLinkOrIndexFormKey(comparisonValueObject)
                    : null,
                RunOnType = GetPropertyValue(data, "RunOnType")?.ToString() ?? "Subject",
                RunOnTypeIndex = ReadInt32(GetPropertyValue(data, "RunOnTypeIndex"), -1),
                ReferenceFormKey = ReadFormLinkOrIndexFormKey(
                    GetPropertyValue(data, "Reference")),
                UseAliases = ReadBoolean(GetPropertyValue(data, "UseAliases")),
                UsePackageData = ReadBoolean(GetPropertyValue(data, "UsePackageData")),
                FirstUnusedIntParameter = ReadNullableInt32(
                    GetPropertyValue(data, "FirstUnusedIntParameter")),
                SecondUnusedIntParameter = ReadNullableInt32(
                    GetPropertyValue(data, "SecondUnusedIntParameter")),
                FirstUnusedStringParameter = ReadNullableString(
                    GetPropertyValue(data, "FirstUnusedStringParameter")),
                SecondUnusedStringParameter = ReadNullableString(
                    GetPropertyValue(data, "SecondUnusedStringParameter"))
            };
            conditions.Add(condition);
        }
    }

    private static object? GetPropertyValue(object? value, string propertyName) =>
        value?.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);

    private static float ConvertToSingle(object? value) => value switch
    {
        null => 0f,
        float number => number,
        double number => (float)number,
        decimal number => (float)number,
        _ when float.TryParse(
            value.ToString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var number) => number,
        _ => 0f
    };

    private static int ReadInt32(object? value, int defaultValue) =>
        ReadNullableInt32(value) ?? defaultValue;

    private static int? ReadNullableInt32(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static bool ReadBoolean(object? value)
    {
        if (value is bool boolean)
        {
            return boolean;
        }

        return bool.TryParse(value?.ToString(), out var parsed) && parsed;
    }

    private static string? ReadNullableString(object? value)
    {
        var text = value?.ToString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string ReadDataSummary(object? data)
    {
        if (data is null)
        {
            return string.Empty;
        }

        var values = data.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Select(property =>
            {
                object? propertyValue;
                try
                {
                    propertyValue = property.GetValue(data);
                }
                catch
                {
                    return null;
                }

                if (propertyValue is null || propertyValue is IEnumerable && propertyValue is not string)
                {
                    return null;
                }

                if (property.Name.Equals("Keyword", StringComparison.OrdinalIgnoreCase))
                {
                    var keywordFormKey = ReadFormLinkOrIndexFormKey(propertyValue);
                    return keywordFormKey is null
                        ? null
                        : $"{property.Name}={keywordFormKey}";
                }

                if (property.Name.Equals("Weather", StringComparison.OrdinalIgnoreCase))
                {
                    var weatherFormKey = ReadFormLinkOrIndexFormKey(propertyValue);
                    return weatherFormKey is null
                        ? null
                        : $"{property.Name}={weatherFormKey}";
                }

                if (property.Name.Equals("Reference", StringComparison.OrdinalIgnoreCase))
                {
                    var referenceFormKey = ReadFormLinkOrIndexFormKey(propertyValue);
                    return referenceFormKey is null
                        ? null
                        : $"{property.Name}={referenceFormKey}";
                }

                var text = propertyValue.ToString();
                return string.IsNullOrWhiteSpace(text)
                    ? null
                    : $"{property.Name}={text}";
            })
            .Where(value => value is not null)
            .Cast<string>()
            .ToArray();

        return string.Join(", ", values);
    }

    private static string? ReadKeywordFormKey(object? data)
    {
        var keyword = GetPropertyValue(data, "Keyword");
        return ReadKeywordFormKeyFromValue(keyword);
    }

    private static string? ReadWeatherFormKey(object? data)
    {
        var weather = GetPropertyValue(data, "Weather");
        return ReadFormLinkOrIndexFormKey(weather);
    }

    private static string? ReadKeywordFormKeyFromValue(object? keyword)
        => ReadFormLinkOrIndexFormKey(keyword);

    private static string? ReadFormLinkOrIndexFormKey(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var link = GetPropertyValue(value, "Link");
        return ReadFormKey(link) ?? ReadFormKey(value);
    }

    private static string? ReadFormKey(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is IFormLinkGetter formLink &&
            !formLink.IsNull &&
            formLink.FormKeyNullable is { } formKey)
        {
            return formKey.ToString();
        }

        var formKeyNullable = GetPropertyValue(value, "FormKeyNullable");
        if (formKeyNullable is null)
        {
            return null;
        }

        var text = formKeyNullable.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void AddReference(
        ICollection<PluginRecordReferenceSource> references,
        string fieldName,
        object? link)
    {
        if (link is System.Collections.IEnumerable links)
        {
            foreach (var item in links)
            {
                AddReference(references, fieldName, item);
            }

            return;
        }

        if (link is not IFormLinkGetter formLink ||
            formLink.IsNull ||
            formLink.FormKeyNullable is not { } formKey)
        {
            return;
        }

        references.Add(new PluginRecordReferenceSource(fieldName, formKey.ToString()));
    }

    private static void AddAsset(
        ICollection<PluginRecordAssetSource> assets,
        string fieldName,
        object? link)
    {
        if (link is not IAssetLinkGetter assetLink ||
            assetLink.IsNull ||
            string.IsNullOrWhiteSpace(assetLink.GivenPath))
        {
            return;
        }

        assets.Add(new PluginRecordAssetSource(
            fieldName,
            NormalizePath(assetLink.GivenPath)));
    }

    private static string? ReadEditorId(IMajorRecordGetter record)
    {
        var property = record.GetType().GetProperty("EditorID");
        return property?.GetValue(record) as string;
    }

    private static string ReadRecordType(IMajorRecordGetter record)
    {
        const string binaryOverlaySuffix = "BinaryOverlay";
        var typeName = record.GetType().Name;
        return typeName.EndsWith(binaryOverlaySuffix, StringComparison.Ordinal)
            ? typeName[..^binaryOverlaySuffix.Length]
            : typeName;
    }

    private static string NormalizePath(string path) => path
        .Replace('/', '\\')
        .TrimStart('\\');
}
