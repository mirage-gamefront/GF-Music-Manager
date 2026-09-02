using System.Text.RegularExpressions;
using SkyrimScan.Core.Models;

namespace GfMusicManager.Core.Analysis;

/// <summary>
/// Selects only Keyword records that make sense for the
/// GetCombatTargetHasKeyword editor.  The complete Keyword table is far too
/// broad: it also contains food, crafting, animation and item metadata.
/// </summary>
public static class MusicCombatKeywordCandidateFilter
{
    private static readonly Regex TokenPattern = new(
        "[A-Z]+(?=[A-Z][a-z]|[0-9]|$)|[A-Z]?[a-z]+|[0-9]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<PluginRecordSource> Select(
        IEnumerable<PluginRecordSource> records,
        IEnumerable<MusicConditionSource> conditions)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(conditions);

        var keywordRecords = records
            .Where(record => record.RecordType.Equals("Keyword", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var referencedFormKeys = conditions
            .Where(condition => condition.FunctionName.Equals(
                "GetCombatTargetHasKeyword",
                StringComparison.OrdinalIgnoreCase))
            .Select(condition => condition.KeywordFormKey)
            .Where(formKey => !string.IsNullOrWhiteSpace(formKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return keywordRecords
            .Where(record => referencedFormKeys.Contains(record.FormKey) || IsCombatKeyword(record))
            .OrderBy(record => record.EditorId ?? record.FormKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsCombatKeyword(PluginRecordSource record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.RecordType.Equals("Keyword", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var editorId = record.EditorId;
        if (string.IsNullOrWhiteSpace(editorId))
        {
            return false;
        }

        var tokens = TokenPattern.Matches(editorId)
            .Select(match => match.Value)
            .ToArray();
        if (tokens.Length == 0)
        {
            return false;
        }

        for (var index = 0; index + 1 < tokens.Length; index++)
        {
            if (tokens[index].Equals("Actor", StringComparison.OrdinalIgnoreCase) &&
                tokens[index + 1].Equals("Type", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (tokens.Any(token => token.Equals("Non", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (tokens.Any(token => token.Equals("Combat", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return tokens.Any(token => token.Equals("Civil", StringComparison.OrdinalIgnoreCase)) &&
               tokens.Any(token => token.Equals("War", StringComparison.OrdinalIgnoreCase));
    }
}
