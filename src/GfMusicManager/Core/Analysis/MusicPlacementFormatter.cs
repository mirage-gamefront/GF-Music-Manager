using GfMusicManager.Core.Localization;

namespace GfMusicManager.Core.Analysis;

public static class MusicPlacementFormatter
{
    public const int CompactMaxLength = 160;

    private static readonly MusicSettingScope[] ScopeOrder =
    {
        MusicSettingScope.WorldSpace,
        MusicSettingScope.Location,
        MusicSettingScope.Region,
        MusicSettingScope.Cell
    };

    public static string Format(IReadOnlyList<MusicSettingSource> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var entries = BuildSummaryEntries(settings);
        if (entries.Count == 0)
        {
            return UiText.Get("Analysis.Placement.AudioOnlyUnresolved");
        }

        var summary = string.Join(UiText.Get("Analysis.Placement.SummarySeparator"), entries);
        return summary.Length <= CompactMaxLength
            ? summary
            : string.Join(
                UiText.Get("Analysis.Placement.SummarySeparator"),
                BuildCountOnlySummaryEntries(settings));
    }

    public static string FormatCount(IReadOnlyList<MusicSettingSource> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return UiText.Format("Analysis.Placement.Count", settings.Count);
    }

    public static string FormatDetailed(IReadOnlyList<MusicSettingSource> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Count == 0)
        {
            return UiText.Get("Analysis.Placement.AudioOnlyUnresolved");
        }

        var lines = new List<string>();
        foreach (var group in settings
                     .GroupBy(setting => setting.Scope)
                     .OrderBy(group => ScopeRank(group.Key)))
        {
            var names = group
                .Select(setting => setting.ScopeName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            lines.Add(UiText.Format(
                "Analysis.Placement.DetailedGroup",
                group.Key.ToLabel(),
                names.Length,
                FormatNameList(names, 8)));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> BuildSummaryEntries(
        IReadOnlyList<MusicSettingSource> settings)
    {
        var scopedSettings = settings
            .Where(setting => setting.Scope != MusicSettingScope.MusicType)
            .ToArray();
        if (scopedSettings.Length > 0)
        {
            return ScopeOrder
                .Where(scope => scopedSettings.Any(setting => setting.Scope == scope))
                .Select(scope => FormatScopeGroup(scopedSettings, scope))
                .ToArray();
        }

        var musicTypeNames = settings
            .Where(setting => setting.Scope == MusicSettingScope.MusicType)
            .Select(setting => setting.ScopeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return musicTypeNames.Length == 0
            ? Array.Empty<string>()
            : new[] { FormatNames(MusicSettingScope.MusicType.ToLabel(), musicTypeNames) };
    }

    private static IReadOnlyList<string> BuildCountOnlySummaryEntries(
        IReadOnlyList<MusicSettingSource> settings)
    {
        var scopedSettings = settings
            .Where(setting => setting.Scope != MusicSettingScope.MusicType)
            .ToArray();
        if (scopedSettings.Length == 0)
        {
            return BuildSummaryEntries(settings);
        }

        return ScopeOrder
            .Where(scope => scopedSettings.Any(setting => setting.Scope == scope))
            .Select(scope =>
            {
                var count = scopedSettings.Count(setting => setting.Scope == scope);
                return UiText.Format("Analysis.Placement.ScopeCount", scope.ToLabel(), count);
            })
            .ToArray();
    }

    private static string FormatScopeGroup(
        IReadOnlyList<MusicSettingSource> settings,
        MusicSettingScope scope)
    {
        var names = settings
            .Where(setting => setting.Scope == scope)
            .Select(setting => setting.ScopeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return FormatNames(scope.ToLabel(), names, visibleNameCount: 1);
    }

    private static string FormatNames(
        string label,
        IReadOnlyList<string> names,
        int visibleNameCount = 2) =>
        UiText.Format(
            "Analysis.Placement.Group",
            label,
            FormatNameList(names, visibleNameCount));

    private static string FormatNameList(
        IReadOnlyList<string> names,
        int visibleNameCount)
    {
        var visibleNames = names.Take(visibleNameCount).ToArray();
        var summary = string.Join(
            UiText.Get("Analysis.Placement.NameSeparator"),
            visibleNames);
        return names.Count > visibleNameCount
            ? UiText.Format(
                "Analysis.Placement.MoreCount",
                summary,
                names.Count - visibleNameCount)
            : summary;
    }

    private static int ScopeRank(MusicSettingScope scope) => scope switch
    {
        MusicSettingScope.WorldSpace => 0,
        MusicSettingScope.Location => 1,
        MusicSettingScope.Region => 2,
        MusicSettingScope.Cell => 3,
        MusicSettingScope.MusicType => 4,
        _ => 5
    };

    private static string ToLabel(this MusicSettingScope scope) => scope switch
    {
        MusicSettingScope.WorldSpace => UiText.Get("Scope.WorldSpace"),
        MusicSettingScope.Location => UiText.Get("Scope.Location"),
        MusicSettingScope.Region => UiText.Get("Scope.Region"),
        MusicSettingScope.Cell => UiText.Get("Scope.Cell"),
        MusicSettingScope.MusicType => UiText.Get("Scope.MusicType"),
        _ => scope.ToString()
    };
}
