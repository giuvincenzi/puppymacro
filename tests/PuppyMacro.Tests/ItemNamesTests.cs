using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

public class ItemNamesTests
{
    [Fact]
    public void A_copy_gets_the_first_free_number_compared_without_case()
    {
        Assert.Equal("Farm (2)", ItemNames.Copy("Farm", new[] { "Farm" }));
        Assert.Equal("Farm (3)", ItemNames.Copy("Farm", new[] { "Farm", "farm (2)" }));
        Assert.Equal("Farm (2)", ItemNames.Copy("  Farm ", new[] { "Farm" }));
    }

    [Fact]
    public void A_copy_of_a_numbered_name_counts_on_instead_of_adding_a_number()
    {
        Assert.Equal("Farm (3)", ItemNames.Copy("Farm (2)", new[] { "Farm", "Farm (2)" }));
        Assert.Equal("Farm (2)", ItemNames.Copy("Farm (5)", new[] { "Farm (5)" }));
        // A number that is part of the name, not in brackets at its end, stays.
        Assert.Equal("Level 2 (2)", ItemNames.Copy("Level 2", new[] { "Level 2" }));
    }
}
