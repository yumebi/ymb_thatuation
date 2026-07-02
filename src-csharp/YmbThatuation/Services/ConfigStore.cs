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
        Config cfg;
        try
        {
            var json = File.ReadAllText(_configPath);
            cfg = JsonSerializer.Deserialize<Config>(json) ?? Config.CreateDefault();
        }
        catch
        {
            // ファイルが無い/壊れている場合は既定値を使う
            cfg = Config.CreateDefault();
        }

        MigrateForceRenavigate(cfg);
        return cfg;
    }

    /// <summary>
    /// force_renavigate(真っ白画面の再読み込みキック)がC#移植版で未実装だった期間、
    /// 有効化しても効果が無かったため、keep_awakeサービスに限り一度だけ既定で有効化する。
    /// 既に移行済みなら何もしない(ユーザーが個別にオフにした設定を勝手に戻さないため)。
    /// </summary>
    private static void MigrateForceRenavigate(Config cfg)
    {
        if (cfg.Settings.ForceRenavigateMigrated) return;

        foreach (var inst in cfg.Instances.Where(i => i.KeepAwake))
        {
            inst.ForceRenavigate = true;
        }
        cfg.Settings.ForceRenavigateMigrated = true;
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
