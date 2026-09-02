using GfMusicManager.Core.Analysis;
using GfMusicManager.Core.Generation;
using GfMusicManager.Core.Planning;
using SkyrimScan.Core.Models;
using Xunit;

namespace GfMusicManager.Core.Tests;

public sealed class MusicGenerationCapacityTests
{
    [Fact]
    public void Estimate_UsesOnePluginWhenTracksFitWithinTheCapacity()
    {
        var plan = CreatePlan(296);

        var estimate = new MusicGenerationCapacityPlanner().Estimate(
            plan,
            MusicGenerationCapacityPolicy.CurrentAe);

        Assert.Equal(296, estimate.AdoptedAssetCount);
        Assert.Equal(296, estimate.NewMusicTrackRecordCount);
        Assert.False(estimate.RequiresSplit);
        var plugin = Assert.Single(estimate.Plugins);
        Assert.Equal("GF Music Product.esp", plugin.PluginFileName);
        Assert.Equal(296, plugin.NewRecordCount);
        Assert.Equal(296, plugin.AssetKeys.Count);
    }

    [Fact]
    public void Estimate_SplitsTracksAtThePerPluginCapacity()
    {
        var plan = CreatePlan(4097);

        var estimate = new MusicGenerationCapacityPlanner().Estimate(
            plan,
            MusicGenerationCapacityPolicy.CurrentAe);

        Assert.Equal(2, estimate.Plugins.Count);
        Assert.Equal(4096, estimate.Plugins[0].NewRecordCount);
        Assert.Equal(1, estimate.Plugins[1].NewRecordCount);
        Assert.Equal("GF Music Product.esp", estimate.Plugins[0].PluginFileName);
        Assert.Equal("GF Music Product - 02.esp", estimate.Plugins[1].PluginFileName);
        Assert.Equal(4097, estimate.Plugins.SelectMany(plugin => plugin.AssetKeys).Count());
    }

    [Fact]
    public void Estimate_UsesTheLegacyCapacityWhenRequested()
    {
        var plan = CreatePlan(4097);

        var estimate = new MusicGenerationCapacityPlanner().Estimate(
            plan,
            MusicGenerationCapacityPolicy.Legacy);

        Assert.Equal(3, estimate.Plugins.Count);
        Assert.Equal(2048, estimate.Plugins[0].NewRecordCount);
        Assert.Equal(2048, estimate.Plugins[1].NewRecordCount);
        Assert.Equal(1, estimate.Plugins[2].NewRecordCount);
    }

    [Fact]
    public void Estimate_DoesNotCountWorldSpaceOverridesAsNewRecords()
    {
        var plan = new MusicGenerationPlan();
        plan.GetOrCreate(
            Asset("Worldspace Music", @"music\worldspace.xwm"),
            new[] { CreateWorldspaceDestination("000100:Fixture.esp") });

        var estimate = new MusicGenerationCapacityPlanner().Estimate(
            plan,
            MusicGenerationCapacityPolicy.CurrentAe);

        Assert.Equal(1, estimate.NewRecordCount);
        Assert.Equal(1, estimate.WorldSpaceOverrideCount);
        Assert.Single(estimate.Plugins);
        Assert.Equal(1, estimate.Plugins[0].NewRecordCount);
    }

    [Fact]
    public void Estimate_CountsIntegrationMusicTypesOnTheFirstPlugin()
    {
        var plan = CreatePlan(4095);

        var estimate = new MusicGenerationCapacityPlanner().Estimate(
            plan,
            MusicGenerationCapacityPolicy.CurrentAe,
            additionalMusicTypeRecordCount: 1);

        Assert.Single(estimate.Plugins);
        Assert.Equal(4095, estimate.Plugins[0].NewMusicTrackRecordCount);
        Assert.Equal(1, estimate.Plugins[0].NewMusicTypeRecordCount);
        Assert.Equal(4096, estimate.Plugins[0].NewRecordCount);
    }

    [Fact]
    public void Estimate_TracksAggregateCountAcrossTheCapacityBoundary()
    {
        var plan = CreatePlan(4096);

        var estimate = new MusicGenerationCapacityPlanner().Estimate(
            plan,
            MusicGenerationCapacityPolicy.CurrentAe,
            additionalMusicTypeRecordCount: 1);

        Assert.Equal(4096, estimate.NewMusicTrackRecordCount);
        Assert.Equal(
            estimate.NewMusicTrackRecordCount,
            estimate.Plugins.Sum(plugin => plugin.NewMusicTrackRecordCount));
        Assert.Equal(4097, estimate.NewRecordCount);
        Assert.Equal(4095, estimate.Plugins[0].NewMusicTrackRecordCount);
        Assert.Equal(1, estimate.Plugins[1].NewMusicTrackRecordCount);
    }

    [Fact]
    public void Estimate_BlocksSplitWhenPolicyDisallowsIt()
    {
        var plan = CreatePlan(4097);

        var estimate = new MusicGenerationCapacityPlanner().Estimate(
            plan,
            new MusicGenerationCapacityPolicy(4096, AllowSplit: false));

        Assert.True(estimate.RequiresSplit);
        Assert.True(estimate.IsBlockedByCapacity);
        Assert.False(estimate.IsValid);
    }

    [Fact]
    public void GetPluginFileName_RejectsNonPositiveIndexes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MusicGenerationCapacityPlanner.GetPluginFileName(0));
    }

    private static MusicGenerationPlan CreatePlan(int count)
    {
        var plan = new MusicGenerationPlan();
        for (var index = 0; index < count; index++)
        {
            plan.GetOrCreate(
                Asset(
                    $"Music Mod {index:0000}",
                    $@"music\fixture\track_{index:0000}.xwm"),
                Array.Empty<MusicSettingSource>());
        }

        return plan;
    }

    private static AssetSource Asset(string modName, string virtualPath) =>
        new(
            virtualPath,
            AssetSourceKind.Loose,
            modName,
            $@"C:\Fixture\{modName}",
            true,
            $@"C:\Fixture\{modName}\{virtualPath.Replace('\\', Path.DirectorySeparatorChar)}",
            null,
            1);

    private static MusicSettingSource CreateWorldspaceDestination(string formKey)
    {
        var plugin = new PluginSource(
            "Fixture.esp",
            @"C:\Fixture\Fixture.esp",
            "Fixture",
            @"C:\Fixture",
            true,
            true,
            1,
            1);
        var worldspace = new PluginRecordSource(
            formKey,
            "Worldspace",
            "Worldspace_Fixture",
            false,
            plugin,
            true);
        var musicType = new PluginRecordSource(
            "000101:Fixture.esp",
            "MusicType",
            "MUSExploreFixture",
            false,
            plugin,
            true);

        return new MusicSettingSource(
            MusicSettingScope.WorldSpace,
            worldspace.FormKey,
            worldspace.EditorId,
            musicType.FormKey,
            musicType.EditorId,
            worldspace,
            musicType,
            Array.Empty<MusicTrackSource>());
    }
}
