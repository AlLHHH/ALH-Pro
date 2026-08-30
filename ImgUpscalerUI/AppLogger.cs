using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ALHPro;

/// <summary>
/// 诊断日志:单文件记录用户操作与报错(含原因/堆栈),供用户分享排障。
/// 路径:%LOCALAPPDATA%\ALHPro\diagnostic.log
/// 清理策略(两项复选框,相辅相成):按时间(丢弃早于 N 天的行)+ 按大小(超限只留最新行)。
/// </summary>
public static class AppLogger
{
    private static readonly object Lock = new();
    private static string? _logFile;
    private static LogSettings _settings = new();

    /// <summary>日志文件路径(LocalAppData\ALHPro\diagnostic.log)。</summary>
    public static string LogFile
    {
        get
        {
            if (_logFile == null)
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALHPro");
                Directory.CreateDirectory(dir);
                _logFile = Path.Combine(dir, "diagnostic.log");
            }
            return _logFile;
        }
    }

    /// <summary>日志目录(LocalAppData\ALHPro)。</summary>
    public static string LogDir => Path.GetDirectoryName(LogFile)!;

    // 清理配置
    public static bool CleanByTime { get => _settings.CleanByTime; set => _settings.CleanByTime = value; }
    public static int KeepDays { get => _settings.KeepDays; set => _settings.KeepDays = Math.Clamp(value, 1, 30); }
    public static bool CleanBySize { get => _settings.CleanBySize; set => _settings.CleanBySize = value; }
    public static int MaxSizeMb { get => _settings.MaxSizeMb; set => _settings.MaxSizeMb = Math.Clamp(value, 1, 20); }

    private static string SettingsFile => Path.Combine(LogDir, "log-settings.json");

    /// <summary>读取清理配置(启动时调用,失败用默认值)。</summary>
    public static void LoadConfig()
    {
        try
        {
            if (File.Exists(SettingsFile))
                _settings = JsonSerializer.Deserialize<LogSettings>(File.ReadAllText(SettingsFile)) ?? new LogSettings();
        }
        catch { /* 读取失败用默认 */ }
    }

    /// <summary>保存清理配置。</summary>
    public static void SaveConfig()
    {
        try
        {
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_settings));
        }
        catch { /* 保存失败忽略 */ }
    }

    /// <summary>记录一条普通操作日志。</summary>
    public static void Info(string msg) => Write("INFO ", msg);

    /// <summary>记录一条错误日志(含原因与堆栈)。错误级日志【同步写盘】——异常/崩溃瞬间资源紧张,
    /// 走后台线程可能来不及落盘就没了,同步写保证"崩溃前最后一行"一定在日志里。</summary>
    public static void Error(string msg, Exception? ex = null)
    {
        var sb = new StringBuilder(msg);
        if (ex != null)
        {
            sb.Append(" | 原因: ").Append(ex.Message);
            if (ex.InnerException != null) sb.Append(" → ").Append(ex.InnerException.Message);
            sb.AppendLine();
            sb.Append(ex);
        }
        Write("ERROR", sb.ToString(), sync: true);
    }

    private static void Write(string level, string msg, bool sync = false)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {msg}{Environment.NewLine}";
        try
        {
            // 后台线程写文件,避免磁盘 IO 卡 UI(日志行数多时很关键);
            // sync=true(错误级)直接同步写,崩溃/进程被杀也不丢日志。
            Action write = () =>
            {
                try
                {
                    lock (Lock)
                    {
                        // UTF-8 带 BOM:记事本/大多数编辑器能正确显示中文(无 BOM 会被按 ANSI 猜 → 乱码)
                        File.AppendAllText(LogFile, line, new UTF8Encoding(true));
                    }
                }
                catch { /* 日志写失败不影响主流程 */ }
            };
            if (sync) write();
            else Task.Run(write);
        }
        catch { /* 任务创建失败忽略 */ }
    }

    /// <summary>
    /// 启动时调用:若日志文件是旧的"无 BOM"编码,整体转成 UTF-8 带 BOM(否则记事本看中文乱码)。
    /// 只转一次:无 BOM 才转,有 BOM 跳过。
    /// </summary>
    public static void EnsureUtf8Bom()
    {
        try
        {
            if (!File.Exists(LogFile)) return;
            var bytes = File.ReadAllBytes(LogFile);
            // UTF-8 BOM = EF BB BF;无 BOM 且含非 ASCII 字节才转(纯 ASCII 文件不需要)
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return;
            bool hasNonAscii = false;
            foreach (var b in bytes) { if (b > 127) { hasNonAscii = true; break; } }
            if (!hasNonAscii) return;
            var text = new UTF8Encoding(false).GetString(bytes);   // 旧文件本来就是 UTF-8 无 BOM 写的
            lock (Lock)
            {
                File.WriteAllText(LogFile, text, new UTF8Encoding(true));
            }
        }
        catch { /* 转码失败忽略 */ }
    }

    /// <summary>
    /// 按配置清理日志(启动时调用一次):
    /// 1) 按时间:丢弃早于 KeepDays 天的行;
    /// 2) 按大小:若仍超过 MaxSizeMb,只保留最新的部分(约控制在限制内)。
    /// </summary>
    public static void Cleanup()
    {
        try
        {
            if (!File.Exists(LogFile)) return;
            var lines = File.ReadAllLines(LogFile);
            if (lines.Length == 0) return;

            var now = DateTime.Now;
            var kept = new List<string>(lines.Length);

            // 时间清理
            var cutoff = now.AddDays(-KeepDays);
            foreach (var line in lines)
            {
                if (CleanByTime && TryParseTime(line, out var t) && t < cutoff) continue;
                kept.Add(line);
            }

            // 大小清理:超限只留最新部分
            if (CleanBySize && MaxSizeMb > 0)
            {
                long limit = (long)MaxSizeMb * 1024 * 1024;
                long total = 0;
                foreach (var l in kept) total += l.Length + 2;
                if (total > limit)
                {
                    var newest = new List<string>();
                    long acc = 0;
                    for (int i = kept.Count - 1; i >= 0; i--)
                    {
                        acc += kept[i].Length + 2;
                        newest.Add(kept[i]);
                        if (acc >= limit) break;
                    }
                    newest.Reverse();
                    kept = newest;
                }
            }

            if (kept.Count != lines.Length)
                File.WriteAllLines(LogFile, kept, new UTF8Encoding(true));
        }
        catch { /* 清理失败忽略 */ }
    }

    /// <summary>当前日志文件大小(字节),供界面显示。</summary>
    public static long CurrentSize
    {
        get
        {
            try { return File.Exists(LogFile) ? new FileInfo(LogFile).Length : 0; }
            catch { return 0; }
        }
    }

    /// <summary>用资源管理器打开日志文件所在目录并选中该文件;目录打不开时降级为直接打开目录;
    /// 仍失败则用记事本打开日志文件。失败原因写进日志(不再静默吞)。</summary>
    public static void OpenInExplorer()
    {
        try
        {
            Directory.CreateDirectory(LogDir);   // 确保目录存在(否则 explorer /select 无反应)
            try
            {
                var p = new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{LogFile}\"")
                {
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(p);
                return;
            }
            catch
            {
                // /select 选中文件失败(路径/权限问题)→ 直接打开目录
                var p2 = new System.Diagnostics.ProcessStartInfo(LogDir)
                {
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(p2);
            }
        }
        catch (Exception ex)
        {
            // 都失败:用记事本打开日志文件(至少能看到日志内容)
            Info("打开日志文件夹失败(降级记事本):" + ex.Message);
            try
            {
                var p3 = new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{LogFile}\"")
                {
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(p3);
            }
            catch { }
        }
    }

    private static bool TryParseTime(string line, out DateTime t)
    {
        t = default;
        // 行首形如 "[2026-08-25 10:30:00]"
        if (line.Length >= 20 && line[0] == '[')
            return DateTime.TryParse(line.Substring(1, 19), out t);
        return false;
    }

    private sealed class LogSettings
    {
        public bool CleanByTime { get; set; } = true;
        public int KeepDays { get; set; } = 30;
        public bool CleanBySize { get; set; } = true;
        public int MaxSizeMb { get; set; } = 10;
    }
}
