using System.IO;
using Xunit;
using YmbThatuation.Services;

namespace YmbThatuation.Tests;

public class DiagnosticLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "thatu_diag_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 後始末の失敗は無視 */ }
    }

    [Fact]
    public void Write_CreatesDirectoryAndAppendsLine()
    {
        var path = Path.Combine(_dir, "sub", "diagnostic.log");
        var log = new DiagnosticLog(path, 1024 * 1024);

        log.Write("notify", "title=a");
        log.Write("notify", "title=b");

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("[notify] title=a", lines[0]);
        Assert.Contains("[notify] title=b", lines[1]);
    }

    [Fact]
    public void Write_RotatesToOldFile_WhenLimitReached()
    {
        var path = Path.Combine(_dir, "diagnostic.log");
        var log = new DiagnosticLog(path, 100);

        for (var i = 0; i < 10; i++) log.Write("k", new string('x', 40));

        Assert.True(File.Exists(path + ".old"));
        Assert.True(new FileInfo(path).Length < 400);
    }

    [Fact]
    public void Write_DoesNotThrow_WhenPathIsInvalid()
    {
        var log = new DiagnosticLog("\0:invalid", 100);

        log.Write("k", "v");
    }

    [Theory]
    [InlineData(null, 3, "")]
    [InlineData("abc", 3, "abc")]
    [InlineData("a\r\nb", 10, "a  b")]
    [InlineData("abcdef", 3, "abc…")]
    public void Clip_FlattensAndTruncates(string? input, int max, string expected)
    {
        Assert.Equal(expected, DiagnosticLog.Clip(input, max));
    }
}
