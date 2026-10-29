using AntSim.Core.Worlds;

namespace AntSim.Core.Tests;

public class WorldRulesTests
{
    private static readonly WorldRules Rules = WorldRules.Default;

    [Theory]
    [InlineData(0, 0, WorldError.None)]
    [InlineData(10, 0, WorldError.None)]
    [InlineData(0, 10, WorldError.None)]
    [InlineData(4, 6, WorldError.None)]
    [InlineData(5, 6, WorldError.QuantityLimitExceeded)]
    [InlineData(11, 0, WorldError.QuantityLimitExceeded)]
    public void QuantitySum_IsLimitedToTen(byte food, byte larva, WorldError expected)
    {
        Assert.Equal(10, Rules.MaxQuantityPerTile);
        Assert.Equal(expected, Rules.Validate(new TileState(Terrain.Dirt, SolidBlock.None, food, larva)));
    }

    [Theory]
    [InlineData(SolidBlock.Soil)]
    [InlineData(SolidBlock.Rock)]
    public void Solid_CannotCoexistWithQuantityBlocks(SolidBlock solid)
    {
        Assert.Equal(WorldError.None, Rules.Validate(new TileState(Terrain.Water, solid, 0, 0)));
        Assert.Equal(WorldError.SolidOccupied, Rules.Validate(new TileState(Terrain.Dirt, solid, 1, 0)));
        Assert.Equal(WorldError.SolidOccupied, Rules.Validate(new TileState(Terrain.Dirt, solid, 0, 1)));
    }

    [Fact]
    public void Limit_IsConfigurableWithinByteRange()
    {
        var rules = new WorldRules { MaxQuantityPerTile = 3 };
        Assert.Equal(WorldError.QuantityLimitExceeded, rules.Validate(new TileState(Terrain.Dirt, SolidBlock.None, 2, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldRules { MaxQuantityPerTile = 256 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldRules { MaxQuantityPerTile = -1 });
    }
}
