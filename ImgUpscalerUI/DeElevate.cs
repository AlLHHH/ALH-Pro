// DeElevate.cs — 提权自愈:一旦发现自己在以管理员权限运行,就用**普通用户令牌**重新拉起自己。
//
// 【为什么要这么做 · 2026-09-18 用户反馈"很多设备没开管理员也不能把项目拖入软件"】
// Windows 的 UIPI(完整性级别隔离)规定:**拖放的源与目标必须在同一权限级别**。
// WindowsAppSDK 官方已明确结论:**"Drag-and-drop in elevated WinUI3 applications is not supported"**
// (microsoft/WindowsAppSDK issue #4433)—— 也就是说,提权状态下**没有任何 API 能收到拖放**
// (网上那些钩 WM_DROPFILES 的办法对 WinUI3 的 OLE 拖放无效)。
// 而本软件**不需要管理员权限**(安装脚本 PrivilegesRequired=lowest,写完自己的目录/输出目录即可)。
//
// 所以"最优解"只有一个方向:**保证程序跑在普通权限上**。
//   · 新装机器:安装脚本 [Run] 已加 runasoriginaluser(装完那次启动不再继承管理员令牌)✓
//   · **已经装好的机器**:程序当初是从装完界面以管理员令牌启动的 → 用户不重启就一直拖不进去 ✗
//     → 这里自动降权:用资源管理器的令牌重新拉起自己(与安装程序 runasoriginaluser 同一套机制),
//       然后**旧实例立刻退出**,用户无感 ✓
//
// 失败兜底:降权失败就继续以管理员运行,并记一条可执行的告警(不静默)。
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ALHPro;

internal static class DeElevate
{
    /// <summary>降权重启用的命令行标记(子进程靠它避免无限重启;也给单实例锁留出等待窗口)。</summary>
    public const string FlagArg = "--alh-deelevated";

    public static bool HasFlag()
    {
        try
        {
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, FlagArg, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>当前进程是否以管理员权限(提权)运行。</summary>
    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>用普通用户权限重新拉起自己。返回是否成功发起(发起成功后调用方应立刻退出本进程)。</summary>
    public static bool TryRelaunchNormal()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        // 【必须绝对路径】CreateProcessAsUser 的 lpApplicationName **不会**去 PATH 里找 ——
        // 传相对/裸文件名会直接 ERROR_FILE_NOT_FOUND(2)。实测验证时就在这踩过一次 ✗,这里做一道保险。
        try { exe = System.IO.Path.GetFullPath(exe); } catch { }

        // ① 首选:用「外壳(shell=资源管理器)的令牌」创建进程 —— 这是安装程序 runasoriginaluser 用的同一套办法,
        //    能拿到明确的成功/失败,且子进程的桌面/会话与用户正常启动完全一致。
        if (TryLaunchWithShellToken(exe)) return true;

        // ② 兜底:交给资源管理器去启动(同样会以普通权限运行)。这条路径拿不到返回值,但实践中很可靠。
        try
        {
            var psi = new ProcessStartInfo("explorer.exe", "\"" + exe + "\" " + FlagArg)
            {
                UseShellExecute = false,
            };
            Process.Start(psi);
            AppLogger.Warn("降权重启:已改用 explorer.exe 方式启动普通权限实例(未拿到返回码)");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("降权重启失败(explorer 兜底也不行):" + ex.Message);
            return false;
        }
    }

    // ---------------- 外壳令牌启动(与 Inno Setup runasoriginaluser 同源) ----------------
    private static bool TryLaunchWithShellToken(string exe)
    {
        IntPtr shellWnd = IntPtr.Zero, shellProc = IntPtr.Zero, shellTok = IntPtr.Zero, newTok = IntPtr.Zero;
        try
        {
            shellWnd = GetShellWindow();
            if (shellWnd == IntPtr.Zero) return false;
            _ = GetWindowThreadProcessId(shellWnd, out uint pid);
            if (pid == 0) return false;

            shellProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (shellProc == IntPtr.Zero) return false;
            if (!OpenProcessToken(shellProc, TOKEN_DUPLICATE | TOKEN_QUERY, out shellTok)) return false;

            // 复制成主令牌:CreateProcessAsUser 只接受主令牌
            if (!DuplicateTokenEx(shellTok, TOKEN_ALL_ACCESS, IntPtr.Zero,
                                  SecurityImpersonation, TokenPrimary, out newTok))
                return false;

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",   // 必须指定交互式桌面,否则子进程窗口不可见
            };
            string cmd = "\"" + exe + "\" " + FlagArg;
            if (!CreateProcessAsUser(newTok, exe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                                     CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero,
                                     System.IO.Path.GetDirectoryName(exe), ref si, out PROCESS_INFORMATION pi))
                return false;

            // 关掉我们这边的句柄(子进程继续跑)
            try { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); } catch { }
            AppLogger.Info("降权重启:已用资源管理器令牌以普通权限启动新实例,本实例即将退出");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("降权重启(外壳令牌)失败:" + ex.Message);
            return false;
        }
        finally
        {
            try { if (newTok != IntPtr.Zero) CloseHandle(newTok); } catch { }
            try { if (shellTok != IntPtr.Zero) CloseHandle(shellTok); } catch { }
            try { if (shellProc != IntPtr.Zero) CloseHandle(shellProc); } catch { }
        }
    }

    // ---------------- P/Invoke ----------------
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ALL_ACCESS = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impLevel, int tokenType, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? app, string cmd, IntPtr pa, IntPtr ta,
        bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
}
