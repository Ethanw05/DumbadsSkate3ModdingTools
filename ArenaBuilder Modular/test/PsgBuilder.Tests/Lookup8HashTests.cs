using ArenaBuilder.Core;

namespace ArenaBuilder.Tests;

public sealed class Lookup8HashTests
{
    /// <summary>
    /// BlenroseMaterial -> 0x297ED8CCD45BEAAB (from Python vault_hash_calculator)
    /// </summary>
    [Fact]
    public void HashString_MatchesPython_BlenroseMaterial()
    {
        ulong hash = Lookup8Hash.HashString("BlenroseMaterial");
        Assert.Equal(0x297ED8CCD45BEAABul, hash);
        Assert.Equal("297ED8CCD45BEAAB", Lookup8Hash.HashStringToHex("BlenroseMaterial"));
    }

    [Fact]
    public void HashStringToHex_Produces16CharUppercase()
    {
        string hex = Lookup8Hash.HashStringToHex("concrete");
        Assert.Equal(16, hex.Length);
        Assert.All(hex, c => Assert.True(char.IsLetterOrDigit(c) && char.IsUpper(c) || char.IsDigit(c)));
    }

    [Fact]
    public void HashString_Empty_ReturnsZero()
    {
        Assert.Equal(0ul, Lookup8Hash.HashString(""));
    }
}
