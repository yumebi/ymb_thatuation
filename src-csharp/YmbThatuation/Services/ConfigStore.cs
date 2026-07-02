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
            var loaded = JsonSerializer.Deserialize<Config>(json);
            if (loaded is not null)
            {
                cfg = loaded;
                BackupConfig(json);
            }
            else
            {
                cfg = Config.CreateDefault();
            }
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
    /// 読み込みに成功した設定ファイルの生JSONを、既知の正常な状態のスナップショットとして
    /// backupsフォルダへ保存する。世代管理として新しい10世代のみ残し、古いものは削除する。
    /// 起動処理に影響させないため、失敗しても黙って無視する。
    /// </summary>
    private void BackupConfig(string json)
    {
        try
        {
            var backupDir = Path.Combine(Path.GetDirectoryName(_configPath)!, "backups");
            Directory.CreateDirectory(backupDir);

            var backupPath = Path.Combine(backupDir, $"config-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(backupPath, json);

            var backups = Directory.GetFiles(backupDir, "config-*.json")
                .OrderByDescending(f => f)
                .Skip(10);
            foreach (var old in backups)
            {
                File.Delete(old);
            }
        }
        catch
        {
            // バックアップ失敗は起動処理に影響させない
        }
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
