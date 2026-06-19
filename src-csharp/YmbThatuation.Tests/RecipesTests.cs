using Xunit;
using YmbThatuation.Models;
using YmbThatuation.Services;

namespace YmbThatuation.Tests;

public class RecipesTests
{
    [Fact]
    public void ResolveUrl_KnownRecipe_ReturnsFixedUrl()
    {
        var cfg = new InstanceCfg { Recipe = "gmail" };
        Assert.Equal("https://mail.google.com", Recipes.ResolveUrl(cfg));
    }

    [Fact]
    public void ResolveUrl_Generic_ReturnsConfiguredUrl()
    {
        var cfg = new InstanceCfg { Recipe = "generic", Url = "https://example.com" };
        Assert.Equal("https://example.com", Recipes.ResolveUrl(cfg));
    }

    [Fact]
    public void ResolveUrl_GenericWithoutUrl_Throws()
    {
        var cfg = new InstanceCfg { Recipe = "generic" };
        Assert.Throws<InvalidOperationException>(() => Recipes.ResolveUrl(cfg));
    }

    [Fact]
    public void ResolveUrl_UnknownRecipe_Throws()
    {
        var cfg = new InstanceCfg { Recipe = "no-such-recipe" };
        Assert.Throws<InvalidOperationException>(() => Recipes.ResolveUrl(cfg));
    }

    [Theory]
    [InlineData("gmail", true)]
    [InlineData("slack", false)]
    [InlineData("generic", false)]
    public void DefaultChromeUa_MatchesKnownRecipeSet(string recipe, bool expected)
    {
        Assert.Equal(expected, Recipes.DefaultChromeUa(recipe));
    }

    [Fact]
    public void Letter_KnownRecipe_ReturnsFixedLetter()
    {
        var cfg = new InstanceCfg { Recipe = "chatwork", Name = "Chatwork (A)" };
        Assert.Equal("C", Recipes.Letter(cfg));
    }

    [Fact]
    public void Letter_UnknownRecipe_FallsBackToNameInitial()
    {
        var cfg = new InstanceCfg { Recipe = "generic", Name = "My Service" };
        Assert.Equal("M", Recipes.Letter(cfg));
    }

    [Fact]
    public void Letter_UnknownRecipeAndEmptyName_ReturnsQuestionMark()
    {
        var cfg = new InstanceCfg { Recipe = "generic", Name = "" };
        Assert.Equal("?", Recipes.Letter(cfg));
    }
}
