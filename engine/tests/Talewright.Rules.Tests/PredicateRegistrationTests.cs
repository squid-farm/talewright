using Talewright.Rules.Expressions;

namespace Talewright.Rules.Tests;

public class PredicateRegistrationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("2Bad")]
    [InlineData("Café")]
    [InlineData(".Can")]
    [InlineData("-Can")]
    [InlineData("Can.")]
    [InlineData("Can-")]
    [InlineData("Can..Go")]
    [InlineData("Can--Go")]
    [InlineData("Can.-Go")]
    [InlineData("Can Go")]
    [InlineData("Can+Go")]
    [InlineData("@CanGo")]
    public void Invalid_names_are_rejected_at_registration(string name)
    {
        var registry = new NamedPredicateRegistry();
        var error = Assert.Throws<ArgumentException>(() => registry.Register(name, _ => true));
        Assert.Equal("name", error.ParamName);
        Assert.Empty(registry.Names);
    }

    [Fact]
    public void Null_name_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => new NamedPredicateRegistry().Register(null!, _ => true));

    [Fact]
    public void Valid_names_preserve_case_and_replacement_behavior()
    {
        var registry = new NamedPredicateRegistry().Register("Can.Open-door", _ => true)
            .Register("_CanGo2", _ => true).Register("Can.Open-door", _ => false);
        Assert.True(registry.Contains("Can.Open-door"));
        Assert.False(registry.Contains("can.Open-door"));
        Assert.Equal(2, registry.Names.Count());
    }
}
