using System.IO;
using System.Text.Json;
using YmbThatuation.Models;

namespace YmbThatuation.Services;

public class ConfigStore
{
    private readonly string _configPath;
    private readonly object _lock = new();
    private Config _config;

    public ConfigStore()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "jp.yumebi.thatuation-cs");
        AppDataDir = appData;
        _configPath = Path.Combine(appData, "config", "config.json");
        _config = Load();
    }

    public string AppDataDir { get; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private Config Load()
    {
        try
        {
            var json = File.ReadAllText(_configPath);
            var cfg = JsonSerializer.Deserialize<Config>(json);
            if (cfg != null) return cfg;
        }
        catch
        {
            // ファイルが無い/壊れている場合は既定値を使う
        }
        return Config.CreateDefault();
    }

    public Config Get()
    {
        lock (_lock)
        {
            return _config;
        }
    }

    public void Update(Action<Config> mutate)
    {
        lock (_lock)
        {
            mutate(_config);
            Save();
        }
    }

    /// <summary>設定全体を置き換える(設定インポート用)。</summary>
    public void Replace(Config newConfig)
    {
        lock (_lock)
        {
            _config = newConfig;
            Save();
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, JsonOptions));
    }
}
