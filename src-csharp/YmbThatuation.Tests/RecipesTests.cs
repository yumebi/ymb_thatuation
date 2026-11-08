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

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("smb://evil.example/share/payload.exe")]
    [InlineData("ftp://example.com/x")]
    [InlineData("https://user:pass@example.com")]
    [InlineData("not a url")]
    public void ResolveUrl_GenericWithDisallowedUrl_Throws(string url)
    {
        // 設定JSON(import_settings)からfile://等を仕込まれた場合の防御。
        var cfg = new InstanceCfg { Recipe = "generic", Url = url };
        Assert.Throws<InvalidOperationException>(() => Recipes.ResolveUrl(cfg));
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?a=b")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    public void ResolveUrl_GenericWithAllowedUrl_ReturnsUrl(string url)
    {
        var cfg = new InstanceCfg { Recipe = "generic", Url = url };
        Assert.Equal(url, Recipes.ResolveUrl(cfg));
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

    [Theory]
    [InlineData("chatgpt", "https://chatgpt.com/", "Gp", false)]
    [InlineData("claude", "https://claude.ai/", "Cl", false)]
    [InlineData("gemini", "https://gemini.google.com/", "Ge", true)]
    [InlineData("perplexity", "https://www.perplexity.ai/", "Px", false)]
    [InlineData("copilot", "https://copilot.microsoft.com/", "Cp", false)]
    [InlineData("outlook", "https://outlook.office.com/mail/", "Ol", false)]
    [InlineData("googlemeet", "https://meet.google.com/", "Mt", true)]
    [InlineData("googledocs", "https://docs.google.com/document/u/0/", "Dc", true)]
    [InlineData("googlesheets", "https://docs.google.com/spreadsheets/u/0/", "Sh", true)]
    [InlineData("googleslides", "https://docs.google.com/presentation/u/0/", "Sd", true)]
    [InlineData("todoist", "https://app.todoist.com/", "Td", false)]
    [InlineData("figma", "https://www.figma.com/files/", "Fg", false)]
    [InlineData("deepl", "https://www.deepl.com/translator", "Dl", false)]
    [InlineData("spotify", "https://open.spotify.com/", "Sp", false)]
    [InlineData("youtubemusic", "https://music.youtube.com/", "Ym", true)]
    [InlineData("bluesky", "https://bsky.app/", "Bs", false)]
    [InlineData("facebook", "https://www.facebook.com/", "Fb", true)]
    public void NewRecipes_HaveUrlLetterAndUaDefault(string recipe, string url, string letter, bool chromeUa)
    {
        var cfg = new InstanceCfg { Recipe = recipe, Name = "x" };
        Assert.Equal(url, Recipes.ResolveUrl(cfg));
        Assert.Equal(letter, Recipes.Letter(cfg));
        Assert.Equal(chromeUa, Recipes.DefaultChromeUa(recipe));
        Assert.StartsWith("https://", url);
    }

    [Fact]
    public void EveryRecipeLetter_IsUnique()
    {
        // 文字は、サイドバーのアイコンの代わりに出る。別のレシピと重なると、見分けが付かない。
        var recipes = new[]
        {
            "gmail", "chatwork", "slack", "discord", "messenger", "googlecalendar", "notion", "youtube", "deckblue",
            "misskey", "x", "teams", "googlechat", "whatsapp", "telegram", "linkedin", "instagram", "reddit", "trello",
            "github", "twitch", "zoom", "googledrive", "googlekeep", "threads", "linear",
            "chatgpt", "claude", "gemini", "perplexity", "copilot", "outlook", "googlemeet", "googledocs", "googlesheets", "googleslides", "todoist", "figma", "deepl", "spotify", "youtubemusic", "bluesky", "facebook",
        };
        var letters = recipes.Select(r => Recipes.Letter(new InstanceCfg { Recipe = r, Name = "x" })).ToList();
        Assert.Equal(letters.Count, letters.Distinct().Count());
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
