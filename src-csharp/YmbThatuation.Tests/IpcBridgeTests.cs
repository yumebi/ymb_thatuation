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
}
