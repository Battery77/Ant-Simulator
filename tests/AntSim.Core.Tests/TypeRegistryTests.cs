using AntSim.Core.Content;

namespace AntSim.Core.Tests;

public class TypeRegistryTests
{
    [Fact]
    public void Ids_AreAssignedInRegistrationOrder()
    {
        var registry = new TypeRegistry("tile");
        Assert.Equal(0, registry.Register("a", "A").Id);
        Assert.Equal(1, registry.Register("b", "B").Id);
        Assert.Equal((ushort)1, registry.GetId("b"));
        Assert.Equal("a", registry.Get(0).Key);
    }

    [Fact]
    public void DuplicateKey_Throws()
    {
        var registry = new TypeRegistry("tile");
        registry.Register("a", "A");
        Assert.Throws<ArgumentException>(() => registry.Register("a", "again"));
    }

    [Fact]
    public void UnknownLookups_Fail()
    {
        var registry = new TypeRegistry("tile");
        Assert.False(registry.Contains(0));
        Assert.False(registry.TryGetId("missing", out _));
        Assert.Throws<KeyNotFoundException>(() => registry.GetId("missing"));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Get(0));
    }

    [Fact]
    public void CreateWithEmpty_ReservesIdZero()
    {
        var registry = TypeRegistry.CreateWithEmpty("block");
        Assert.True(registry.HasEmpty);
        Assert.Equal(TypeRegistry.EmptyId, registry.GetId(TypeRegistry.EmptyKey));
        Assert.Equal(1, registry.Register("x", "X").Id);
        Assert.False(new TypeRegistry("block").HasEmpty);
    }
}
