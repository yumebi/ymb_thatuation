using System.Management;

namespace YmbThatuation.Services;

/// <summary>
/// WMI(Win32_Process)でプロセス一覧を取得し、各インスタンスのWebView2プロセスツリー
/// (CoreWebView2.BrowserProcessId をルートとする子孫プロセス群)と
/// アプリ本体のメモリ使用量(MB)を集計する。Tauri版のrefresh_memory相当。
/// </summary>
public class MemoryService
{
    public ulong AppsMemMb { get; private set; }
    public ulong HostMemMb { get; private set; }

    /// <param name="browserProcessIds">生存中インスタンスID → CoreWebView2.BrowserProcessId</param>
    public void Refresh(Dictionary<string, uint> browserProcessIds)
    {
        var processes = new List<(uint Pid, uint ParentPid, string Name, ulong WorkingSetMb)>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, ParentProcessId, Name, WorkingSetSize FROM Win32_Process");
        foreach (ManagementObject mo in searcher.Get())
        {
            var pid = (uint)mo["ProcessId"];
            var parentPid = (uint)mo["ParentProcessId"];
            var name = (string?)mo["Name"] ?? "";
            var workingSet = (ulong)(mo["WorkingSetSize"] ?? 0UL) / (1024 * 1024);
            processes.Add((pid, parentPid, name, workingSet));
        }

        var attributed = new HashSet<uint>();
        ulong appsMb = 0;

        // WebView2は同一Environment配下で複数プロファイルのブラウザプロセスを共有することがあり、
        // その場合は複数インスタンスのBrowserProcessIdが同一になる。
        // 重複を避けるため、共有プロセスツリーごとに1回だけ合計する。
        foreach (var rootPid in browserProcessIds.Values.Distinct())
        {
            var queue = new Queue<uint>();
            queue.Enqueue(rootPid);
            while (queue.Count > 0)
            {
                var pid = queue.Dequeue();
                if (!attributed.Add(pid)) continue;
                var proc = processes.FirstOrDefault(p => p.Pid == pid);
                if (proc.Pid == 0) continue;
                appsMb += proc.WorkingSetMb;
                foreach (var child in processes.Where(p => p.ParentPid == pid))
                {
                    queue.Enqueue(child.Pid);
                }
            }
        }

        var ownPid = (uint)Environment.ProcessId;
        bool IsOwnDescendant(uint pid)
        {
            for (var i = 0; i < 16; i++)
            {
                if (pid == ownPid) return true;
                var proc = processes.FirstOrDefault(p => p.Pid == pid);
                if (proc.Pid == 0) return false;
                pid = proc.ParentPid;
            }
            return false;
        }

        ulong hostMb = 0;
        foreach (var p in processes)
        {
            if (attributed.Contains(p.Pid)) continue;
            if (p.Name.Contains("YmbThatuation", StringComparison.OrdinalIgnoreCase))
            {
                hostMb += p.WorkingSetMb;
                continue;
            }
            if (!p.Name.Contains("msedgewebview2", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (IsOwnDescendant(p.Pid))
            {
                hostMb += p.WorkingSetMb;
            }
        }

        AppsMemMb = appsMb;
        HostMemMb = hostMb;
    }
}
