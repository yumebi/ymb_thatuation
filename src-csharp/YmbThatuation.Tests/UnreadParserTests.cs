using Xunit;
using YmbThatuation.Services;

namespace YmbThatuation.Tests;

public class UnreadParserTests
{
    [Theory]
    [InlineData("Inbox (3) - Gmail", 3u)]
    [InlineData("(5) Chatwork", 5u)]
    [InlineData("(0) Chatwork", 0u)]
    [InlineData("(123) Lots of unread", 123u)]
    public void Parse_ExtractsNumberInParentheses(string title, uint expected)
    {
        Assert.Equal(expected, UnreadParser.Parse(title));
    }

    [Theory]
    [InlineData("No unread here")]
    [InlineData("YMB Thatuation")]
    [InlineData("(abc) not a number")]
    public void Parse_ReturnsZero_WhenNoNumberInParentheses(string title)
    {
        Assert.Equal(0u, UnreadParser.Parse(title));
    }
}
