using GfMusicManager.Core.Localization;

namespace GfMusicManager.Core.Analysis;

public static class MusicScopeNameFormatter
{
    private const string ScopeKeyPrefix = "Analysis.Scope.";

    public static string Format(
        MusicSettingScope scope,
        string technicalName,
        string? recordDisplayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(technicalName);

        var displayName = Clean(recordDisplayName);
        if (!IsJapaneseLanguage)
        {
            if (displayName is null ||
                displayName.Equals(technicalName, StringComparison.OrdinalIgnoreCase))
            {
                return displayName ?? technicalName;
            }

            return UiText.Format(
                ScopeKeyPrefix + "LabelWithTechnicalName",
                displayName,
                technicalName);
        }

        if (scope == MusicSettingScope.Cell &&
            displayName?.Equals("Wilderness", StringComparison.OrdinalIgnoreCase) == true)
        {
            return UiText.Format(
                ScopeKeyPrefix + "Cell.NoNameExterior",
                technicalName);
        }

        var inferredName = Infer(scope, technicalName);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            if (ContainsJapaneseText(displayName) || IsGeneric(scope, inferredName))
            {
                return displayName.Equals(technicalName, StringComparison.OrdinalIgnoreCase)
                    ? displayName
                    : UiText.Format(
                        ScopeKeyPrefix + "LabelWithTechnicalName",
                        displayName,
                        technicalName);
            }

            return UiText.Format(
                ScopeKeyPrefix + "LabelWithTechnicalName",
                inferredName,
                technicalName);
        }

        return UiText.Format(
            ScopeKeyPrefix + "LabelWithTechnicalName",
            inferredName,
            technicalName);
    }

    public static string WithoutMusicTypeSuffix(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var suffix = UiText.Get(ScopeKeyPrefix + "MusicTypeSuffix");
        var technicalNameStart = displayName.IndexOf(
            UiText.Get(ScopeKeyPrefix + "TechnicalNameOpen"),
            StringComparison.Ordinal);
        var readableName = technicalNameStart < 0
            ? displayName
            : displayName[..technicalNameStart];
        if (!readableName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return displayName;
        }

        var shortenedName = readableName[..^suffix.Length];
        return technicalNameStart < 0
            ? shortenedName
            : shortenedName + displayName[technicalNameStart..];
    }

    public static string GetJapaneseLabel(MusicSettingScope scope) =>
        UiText.Get(GetScopeLabelKey(scope));

    private static string Infer(MusicSettingScope scope, string technicalName) => scope switch
    {
        MusicSettingScope.MusicType => InferMusicType(technicalName),
        MusicSettingScope.Cell => InferCell(technicalName),
        MusicSettingScope.Location => InferLocation(technicalName),
        MusicSettingScope.Region => InferRegion(technicalName),
        MusicSettingScope.WorldSpace => InferWorldSpace(technicalName),
        _ => scope.ToString()
    };

    private static string InferMusicType(string technicalName)
    {
        if (TryGetLocalized(
                ScopeKeyPrefix + "MusicType.Exact." + technicalName,
                out var description))
        {
            return description;
        }

        if (technicalName.StartsWith("MUSTown", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Format(
                ScopeKeyPrefix + "MusicType.Dynamic.Town",
                technicalName[7..]);
        }

        if (ContainsToken(technicalName, "CombatBoss"))
        {
            return UiText.Get(ScopeKeyPrefix + "MusicType.Dynamic.CombatBoss");
        }

        if (ContainsToken(technicalName, "Combat"))
        {
            return UiText.Get(ScopeKeyPrefix + "MusicType.Dynamic.Combat");
        }

        if (ContainsToken(technicalName, "Explore"))
        {
            return UiText.Get(ScopeKeyPrefix + "MusicType.Dynamic.Explore");
        }

        if (ContainsToken(technicalName, "Dungeon"))
        {
            return UiText.Get(ScopeKeyPrefix + "MusicType.Dynamic.Dungeon");
        }

        if (ContainsToken(technicalName, "Town"))
        {
            return UiText.Get(ScopeKeyPrefix + "MusicType.Dynamic.TownGeneric");
        }

        return UiText.Get(ScopeKeyPrefix + "MusicType.Generic");
    }

    private static string InferCell(string technicalName)
    {
        if (technicalName.Equals("Wilderness", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Get(ScopeKeyPrefix + "Cell.Wilderness");
        }

        if (TryGetLocalized(
                ScopeKeyPrefix + "Cell.Exact." + technicalName,
                out var description))
        {
            return description;
        }

        foreach (var (token, prefixDescription) in GetLocalizedEntries(
                     ScopeKeyPrefix + "Cell.Prefix."))
        {
            var tokenIndex = technicalName.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (tokenIndex >= 0)
            {
                return prefixDescription + DescribeCellSuffix(
                    technicalName[(tokenIndex + token.Length)..]);
            }
        }

        return UiText.Get(ScopeKeyPrefix + "Label.Cell");
    }

    private static string InferLocation(string technicalName)
    {
        if (TryGetLocalized(
                ScopeKeyPrefix + "Location.Exact." + technicalName,
                out var description))
        {
            return description;
        }

        if (technicalName.EndsWith("Location", StringComparison.OrdinalIgnoreCase))
        {
            var name = technicalName[..^"Location".Length];
            if (string.IsNullOrWhiteSpace(name))
            {
                return UiText.Get(ScopeKeyPrefix + "Location.Generic");
            }

            return UiText.Format(
                ScopeKeyPrefix + "Location.Area",
                TranslateLocationTokens(name));
        }

        return UiText.Get(ScopeKeyPrefix + "Location.Generic");
    }

    private static string InferRegion(string technicalName)
    {
        if (TryGetLocalized(
                ScopeKeyPrefix + "Region.Exact." + technicalName,
                out var description))
        {
            return description;
        }

        const string noPrecipitationSuffix = "NoPrecip";
        if (technicalName.EndsWith(noPrecipitationSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var baseName = technicalName[..^noPrecipitationSuffix.Length];
            var baseDescription = InferRegion(baseName);
            return IsGeneric(MusicSettingScope.Region, baseDescription)
                ? UiText.Get(ScopeKeyPrefix + "Region.NoPrecipitationGeneric")
                : UiText.Format(
                    ScopeKeyPrefix + "Region.NoPrecipitation",
                    baseDescription);
        }

        if (technicalName.StartsWith("Weather", StringComparison.OrdinalIgnoreCase))
        {
            var weatherName = technicalName[7..];
            return UiText.Format(
                ScopeKeyPrefix + "Region.Area",
                TranslateRegionTokens(weatherName));
        }

        return UiText.Get(ScopeKeyPrefix + "Region.Generic");
    }

    private static string InferWorldSpace(string technicalName)
    {
        return TryGetLocalized(
            ScopeKeyPrefix + "WorldSpace.Exact." + technicalName,
            out var description)
            ? description
            : UiText.Get(ScopeKeyPrefix + "WorldSpace.Generic");
    }

    private static string DescribeCellSuffix(string suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return string.Empty;
        }

        if (TryGetLocalized(
                ScopeKeyPrefix + "Cell.Suffix." + suffix,
                out var description))
        {
            return description;
        }

        if (suffix.StartsWith("Exterior", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Format(
                ScopeKeyPrefix + "Cell.Suffix.Exterior",
                suffix[8..]);
        }

        if (suffix.StartsWith("World", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.Format(
                ScopeKeyPrefix + "Cell.Suffix.World",
                suffix[5..]);
        }

        if (suffix.StartsWith("W", StringComparison.OrdinalIgnoreCase) &&
            suffix.Length > 1 &&
            suffix[1..].All(char.IsDigit))
        {
            return UiText.Format(
                ScopeKeyPrefix + "Cell.Suffix.Number",
                suffix[1..]);
        }

        if (suffix.All(char.IsDigit))
        {
            return UiText.Format(
                ScopeKeyPrefix + "Cell.Suffix.Number",
                suffix);
        }

        return UiText.Format(
            ScopeKeyPrefix + "Cell.Suffix.Unknown",
            suffix);
    }

    private static bool ContainsToken(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string TranslateLocationTokens(string value)
    {
        foreach (var (token, replacement) in GetLocalizedEntries(
                     ScopeKeyPrefix + "Location.Token."))
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return replacement;
            }
        }

        return value;
    }

    private static string TranslateRegionTokens(string value)
    {
        foreach (var (token, replacement) in GetLocalizedEntries(
                     ScopeKeyPrefix + "Region.Token."))
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return replacement;
            }
        }

        return value;
    }

    private static bool ContainsJapaneseText(string value) =>
        value.Any(character =>
            (character >= '\u3040' && character <= '\u30ff') ||
            (character >= '\u3400' && character <= '\u9fff'));

    private static bool IsGeneric(MusicSettingScope scope, string value) => scope switch
    {
        MusicSettingScope.MusicType => value.Equals(
            UiText.Get(ScopeKeyPrefix + "MusicType.Generic"),
            StringComparison.Ordinal),
        MusicSettingScope.Cell => value.Equals(
            UiText.Get(ScopeKeyPrefix + "Label.Cell"),
            StringComparison.Ordinal),
        MusicSettingScope.Location => value.Equals(
            UiText.Get(ScopeKeyPrefix + "Location.Generic"),
            StringComparison.Ordinal),
        MusicSettingScope.Region => value.Equals(
                UiText.Get(ScopeKeyPrefix + "Region.Generic"),
                StringComparison.Ordinal) ||
            value.Equals(
                UiText.Get(ScopeKeyPrefix + "Region.NoPrecipitationGeneric"),
                StringComparison.Ordinal),
        MusicSettingScope.WorldSpace => value.Equals(
            UiText.Get(ScopeKeyPrefix + "WorldSpace.Generic"),
            StringComparison.Ordinal),
        _ => false
    };

    private static string GetScopeLabelKey(MusicSettingScope scope) => scope switch
    {
        MusicSettingScope.MusicType => ScopeKeyPrefix + "Label.MusicType",
        MusicSettingScope.Cell => ScopeKeyPrefix + "Label.Cell",
        MusicSettingScope.Location => ScopeKeyPrefix + "Label.Location",
        MusicSettingScope.Region => ScopeKeyPrefix + "Label.Region",
        MusicSettingScope.WorldSpace => ScopeKeyPrefix + "Label.WorldSpace",
        _ => ScopeKeyPrefix + "Label." + scope
    };

    private static bool IsJapaneseLanguage =>
        UiText.Language.Equals(UiLanguage.Japanese, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Token, string Value)> GetLocalizedEntries(string prefix)
    {
        return UiText.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(key => (Token: key[prefix.Length..], Value: UiText.Get(key)))
            .OrderByDescending(entry => entry.Token.Length);
    }

    private static bool TryGetLocalized(string key, out string value)
    {
        var actualKey = UiText.Keys.FirstOrDefault(candidate =>
            candidate.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (actualKey is null)
        {
            value = string.Empty;
            return false;
        }

        value = UiText.Get(actualKey);
        return true;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.TrimStart().StartsWith("$", StringComparison.Ordinal)
            ? null
            : value.Trim();
}
