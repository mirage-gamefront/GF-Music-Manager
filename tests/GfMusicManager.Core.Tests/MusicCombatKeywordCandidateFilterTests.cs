using GfMusicManager.Core.Analysis;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicCombatKeywordCandidateFilterTests
{
    [Fact]
    public void Select_ExcludesUnrelatedKeywordRecords()
    {
        var plugin = Plugin();
        var combat = Record("000001:Fixture.esp", "ActorTypeDragon", plugin);
        var food = Record("000002:Fixture.esp", "aaOsaradeCookedBeef", plugin);
        var crafting = Record("000003:Fixture.esp", "VendorItemPotion", plugin);
        var dungeon = Record("000005:Fixture.esp", "DLCDwarvenPuzzleDungeonIsOrchestrator", plugin);

        var candidates = MusicCombatKeywordCandidateFilter.Select(
            new[] { combat, food, crafting, dungeon },
            Array.Empty<MusicConditionSource>());

        Assert.Contains(candidates, record => record.FormKey == combat.FormKey);
        Assert.DoesNotContain(candidates, record => record.FormKey == food.FormKey);
        Assert.DoesNotContain(candidates, record => record.FormKey == crafting.FormKey);
        Assert.DoesNotContain(candidates, record => record.FormKey == dungeon.FormKey);
    }

    [Fact]
    public void Select_KeepsKeywordReferencedByExistingCombatCondition()
    {
        var plugin = Plugin();
        var custom = Record("000004:Fixture.esp", "FactionSpecificTarget", plugin);
        var condition = MusicConditionSource.CreateCombatKeyword(custom, hasKeyword: true);

        var candidates = MusicCombatKeywordCandidateFilter.Select(
            new[] { custom },
            new[] { condition });

        Assert.Contains(candidates, record => record.FormKey == custom.FormKey);
    }

    private static PluginSource Plugin() => new(
        "Fixture.esp",
        @"C:\Fixture\Fixture.esp",
        "Fixture",
        @"C:\Fixture",
        true,
        true,
        1,
        1);

    private static PluginRecordSource Record(
        string formKey,
        string editorId,
        PluginSource plugin) =>
        new(formKey, "Keyword", editorId, false, plugin, true);
}
