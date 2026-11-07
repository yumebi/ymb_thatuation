using System.IO;
using System.Text.Json;
using Xunit;
using YmbThatuation.Ipc;
using YmbThatuation.Models;
using YmbThatuation.Services;

namespace YmbThatuation.Tests;

public class IpcBridgeTests
{
    private static IpcBridge NewBridge(out string appDataDir, Action<Config>? seed = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ymb-thatuation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new ConfigStore(dir);
        if (seed is not null)
        {
            store.Update(seed);
        }
        appDataDir = dir;
        // get_config/get_translationsのみを使うテストなのでInstanceManagerは不要。
        return new IpcBridge(store, null!, Path.Combine(dir, "wwwroot"));
    }

    [Fact]
    public async Task GetConfig_MasksChatworkToken()
    {
        var bridge = NewBridge(out _, c =>
        {
            var inst = c.Instances.First(i => i.Recipe == "chatwork");
            inst.ChatworkToken = "secret-token-123";
        });

        var json = await bridge.InvokeAsync("get_config", "");
        var root = JsonSerializer.Deserialize<JsonElement>(json);
        var chatwork = root.GetProperty("instances").EnumerateArray()
            .First(i => i.GetProperty("recipe").GetString() == "chatwork");

        Assert.False(chatwork.TryGetProperty("chatwork_token", out var token) && token.ValueKind != JsonValueKind.Null);
        Assert.True(chatwork.GetProperty("chatwork_token_set").GetBoolean());
    }

    [Fact]
    public async Task GetConfig_TokenSetFlagFalse_WhenTokenMissing()
    {
        var bridge = NewBridge(out _);

        var json = await bridge.InvokeAsync("get_config", "");
        var root = JsonSerializer.Deserialize<JsonElement>(json);
        var chatwork = root.GetProperty("instances").EnumerateArray()
            .First(i => i.GetProperty("recipe").GetString() == "chatwork");

        Assert.False(chatwork.GetProperty("chatwork_token_set").GetBoolean());
    }

    [Fact]
    public async Task GetConfig_DoesNotMutateStoredConfig()
    {
        var bridge = NewBridge(out _, c =>
        {
            var inst = c.Instances.First(i => i.Recipe == "chatwork");
            inst.ChatworkToken = "secret-token-123";
        });

        await bridge.InvokeAsync("get_config", "");
        // 取得後も保存側のトークンは残っていること(クローン処理の検証)
        var json2 = await bridge.InvokeAsync("get_config", "");
        var root2 = JsonSerializer.Deserialize<JsonElement>(json2);
        var chatwork2 = root2.GetProperty("instances").EnumerateArray()
            .First(i => i.GetProperty("recipe").GetString() == "chatwork");
        Assert.True(chatwork2.GetProperty("chatwork_token_set").GetBoolean());
    }

    private static async Task<List<string>> InstanceIds(IpcBridge bridge)
    {
        var json = await bridge.InvokeAsync("get_config", "");
        var root = JsonSerializer.Deserialize<JsonElement>(json);
        return root.GetProperty("instances").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString()!).ToList();
    }

    private static string ReorderArgs(IEnumerable<object?> ids) =>
        JsonSerializer.Serialize(new { ids });

    [Fact]
    public async Task ReorderInstances_ChangesOrder()
    {
        var bridge = NewBridge(out _);
        var before = await InstanceIds(bridge);
        Assert.True(before.Count >= 2);
        var reversed = Enumerable.Reverse(before).ToList();

        var result = await bridge.InvokeAsync("reorder_instances", ReorderArgs(reversed));

        Assert.DoesNotContain("__error__", result);
        Assert.Equal(reversed, await InstanceIds(bridge));
    }

    [Fact]
    public async Task ReorderInstances_Persists()
    {
        var bridge = NewBridge(out var dir);
        var reversed = Enumerable.Reverse(await InstanceIds(bridge)).ToList();
        await bridge.InvokeAsync("reorder_instances", ReorderArgs(reversed));

        var reloaded = new ConfigStore(dir).Get().Instances.Select(i => i.Id).ToList();

        Assert.Equal(reversed, reloaded);
    }

    public static IEnumerable<object[]> BadReorders()
    {
        yield return new object[] { "duplicate" };
        yield return new object[] { "missing" };
        yield return new object[] { "unknown" };
        yield return new object[] { "extra" };
        yield return new object[] { "not-array" };
        yield return new object[] { "non-string" };
        yield return new object[] { "no-ids" };
    }

    [Theory]
    [MemberData(nameof(BadReorders))]
    public async Task ReorderInstances_Invalid_ReturnsErrorAndKeepsOrder(string kind)
    {
        var bridge = NewBridge(out var dir);
        var before = await InstanceIds(bridge);
        var args = kind switch
        {
            "duplicate" => ReorderArgs(before.Take(before.Count - 1).Append(before[0]).Cast<object?>()),
            "missing" => ReorderArgs(before.Take(before.Count - 1).Cast<object?>()),
            "unknown" => ReorderArgs(before.Take(before.Count - 1).Append("no-such-id").Cast<object?>()),
            "extra" => ReorderArgs(before.Append("another").Cast<object?>()),
            "not-array" => "{\"ids\":\"abc\"}",
            "non-string" => ReorderArgs(before.Take(before.Count - 1).Cast<object?>().Append(5)),
            _ => "{}",
        };

        var result = await bridge.InvokeAsync("reorder_instances", args);

        Assert.Contains("__error__", result);
        Assert.Equal(before, await InstanceIds(bridge));
        Assert.Equal(before, new ConfigStore(dir).Get().Instances.Select(i => i.Id).ToList());
    }
}
