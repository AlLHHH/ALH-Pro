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

    /// <summary>当前进程是否处于【高完整性】(提权)—— 判的是完整性级别,不是"是不是管理员组成员"。
    ///
    /// 【2026-11-09 为什么改判据】内建 Administrator 账户登录时,explorer 与它启动的一切都在 High;
    /// 而"组成员判定"对**降过完整性级别**的进程仍然返回 true(令牌里还是有 Administrators 组),
    /// 用它当判据会永远以为自己是提权的、永远白重启。UIPI 拦不拦拖放只看完整性级别,
    /// 所以这里改成读 TokenIntegrityLevel(S-1-16-xxxx 的 RID):≥0x3000(High/System) 才算高完整性。</summary>
    public static bool IsElevated()
        => GetIntegrityRid() >= SECURITY_MANDATORY_HIGH_RID;

    /// <summary>当前进程令牌的完整性 RID(Medium=0x2000,High=0x3000,System=0x4000);读不到返回 -1。</summary>
    public static int GetIntegrityRid()
    {
        IntPtr tok = IntPtr.Zero, buf = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out tok)) return -1;
            int len = 0;
            GetTokenInformation(tok, TokenIntegrityLevel, IntPtr.Zero, 0, out len);
            if (len <= 0) return -1;
            buf = Marshal.AllocHGlobal(len);
            if (!GetTokenInformation(tok, TokenIntegrityLevel, buf, len, out len)) return -1;
            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label { PSID Sid; DWORD Attributes; } }
            IntPtr sid = Marshal.ReadIntPtr(buf);
            if (sid == IntPtr.Zero) return -1;
            // 【不要手工拆 SID 的 sub-authority】直接把 SID 转成字符串再取最后一段("S-1-16-8192" → 8192),
            // 少一层指针运算就少一类"读出来是垃圾值"的坑(2026-11-09 第一版就是这么错的)。
            if (!ConvertSidToStringSidW(sid, out IntPtr strPtr) || strPtr == IntPtr.Zero) return -1;
            try
            {
                string? str = Marshal.PtrToStringUni(strPtr);
                if (string.IsNullOrEmpty(str)) return -1;
                int dash = str!.LastIndexOf('-');
                if (dash < 0 || dash + 1 >= str.Length) return -1;
                return int.TryParse(str.Substring(dash + 1), out int rid) ? rid : -1;
            }
            finally { try { LocalFree(strPtr); } catch { } }
        }
        catch { return -1; }
        finally
        {
            if (buf != IntPtr.Zero) try { Marshal.FreeHGlobal(buf); } catch { }
            if (tok != IntPtr.Zero) try { CloseHandle(tok); } catch { }
        }
    }

    /// <summary>用「降完整性级别」的方式重新拉起自己:复制自己的令牌,把 Integrity Level 改成 Medium,
    /// 再 CreateProcessAsUser。
    ///
    /// 【为什么这条路能救内建 Administrator】它不依赖 explorer 的令牌 —— 只把自己的令牌**降级**
    /// (降级不需要任何特权,提级才需要),子进程于是以中完整性运行:组里仍然是 Administrators,
    /// 但 UIPI 眼里它和普通程序一样,可以接收来自(同样是高完整性的)资源管理器的拖放。
    /// 这正是任务管理器「以中等完整性创建此任务」同一套机制。</summary>
    public static bool TryRelaunchAsMediumIntegrity()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        try { exe = System.IO.Path.GetFullPath(exe); } catch { }

        IntPtr selfTok = IntPtr.Zero, dupTok = IntPtr.Zero, sidMem = IntPtr.Zero, labelMem = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(),
                    TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ASSIGN_PRIMARY, out selfTok))
            {
                AppLogger.Warn("降级启动:OpenProcessToken 失败(" + Marshal.GetLastWin32Error() + ")");
                return false;
            }
            if (!DuplicateTokenEx(selfTok, TOKEN_ALL_ACCESS, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out dupTok))
            {
                AppLogger.Warn("降级启动:DuplicateTokenEx 失败(" + Marshal.GetLastWin32Error() + ")");
                return false;
            }
            // S-1-16-8192 = Medium(中完整性):与普通账户启动的程序同一级别
            if (!ConvertStringSidToSidW("S-1-16-8192", out sidMem))
            {
                AppLogger.Warn("降级启动:ConvertStringSidToSid 失败(" + Marshal.GetLastWin32Error() + ")");
                return false;
            }
            // TOKEN_MANDATORY_LABEL: { PSID Sid; DWORD Attributes } —— Attributes 用 SE_GROUP_INTEGRITY
            // TOKEN_MANDATORY_LABEL = { PSID Sid; DWORD Attributes } —— x64 下 16 字节(8+4+4 对齐),
            // 少给会被 SetTokenInformation 拒成 ERROR_BAD_LENGTH(24)。
            int labelSize = ((IntPtr.Size + 4 + IntPtr.Size - 1) / IntPtr.Size) * IntPtr.Size;
            labelMem = Marshal.AllocHGlobal(labelSize);
            Marshal.WriteIntPtr(labelMem, sidMem);
            Marshal.WriteInt32(labelMem + IntPtr.Size, unchecked((int)SE_GROUP_INTEGRITY));
            if (!SetTokenInformation(dupTok, TokenIntegrityLevel, labelMem, labelSize))
            {
                AppLogger.Warn("降级启动:SetTokenInformation(TokenIntegrityLevel) 失败(" + Marshal.GetLastWin32Error() + ")");
                return false;
            }

            var si = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
            };
            string cmd = "\"" + exe + "\" " + FlagArg;
            bool ok = CreateProcessAsUser(dupTok, exe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, System.IO.Path.GetDirectoryName(exe), ref si, out PROCESS_INFORMATION pi);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                // 兜底:CreateProcessWithTokenW(需要 SeImpersonatePrivilege,管理员默认有)
                ok = CreateProcessWithTokenW(dupTok, 0, exe, cmd, CREATE_UNICODE_ENVIRONMENT,
                    IntPtr.Zero, System.IO.Path.GetDirectoryName(exe), ref si, out pi);
                if (!ok)
                {
                    AppLogger.Warn($"降级启动:CreateProcessAsUser 失败({err}),CreateProcessWithTokenW 也失败({Marshal.GetLastWin32Error()})");
                    return false;
                }
                try { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); } catch { }
            }
            else
            {
                // 新实例起来后再确认它没立刻倒下:否则父进程一退,用户看到的就是"点了没反应"。
                uint wait = WaitForSingleObject(pi.hProcess, 1500);
                GetExitCodeProcess(pi.hProcess, out uint code);
                bool healthy = wait == WAIT_TIMEOUT || code == STILL_ACTIVE;
                try { CloseHandle(pi.hThread); CloseHandle(pi.hProcess); } catch { }
                if (!healthy)
                {
                    AppLogger.Warn($"降权重启:新实例启动后立刻退出(exit=0x{code:X8}),本实例继续以管理员身份运行");
                    return false;
                }
            }
            AppLogger.Info("降权重启:已把完整性级别降到 Medium 后启动新实例(内建 Administrator 账户同样适用),本实例即将退出");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("降权重启(降完整性级别)失败:" + ex.Message);
            return false;
        }
        finally
        {
            try { if (labelMem != IntPtr.Zero) Marshal.FreeHGlobal(labelMem); } catch { }
            try { if (sidMem != IntPtr.Zero) LocalFree(sidMem); } catch { }
            try { if (dupTok != IntPtr.Zero) CloseHandle(dupTok); } catch { }
            try { if (selfTok != IntPtr.Zero) CloseHandle(selfTok); } catch { }
        }
    }



    /// <summary>「降权重启」在本机是否已知无效 —— 记住这台机器白重启过。
    ///
    /// 【为什么需要记】2026-11-09 用户诊断包(ALHPro_Diag_20261109_1924,账户 `C:\Users\Administrator`):
    /// 用内建管理员账户登录时,**资源管理器(explorer.exe)本身就是高完整性**,所以拿它的令牌起新进程
    /// 仍然是管理员 ⇒ 每次启动都"降权一次、照样提权、拖放照样不能用",用户只看到窗口闪一下。
    /// 记下来之后就不再每次都重启,只在日志里说明真因。</summary>
    private static string RelaunchIneffectiveFile
        => ParaPaths.SettingsFile("deelevate-ineffective.txt");

    /// <summary>本机是否已知"降权重启无效"(7 天内的记录;太久可能换了账户/关了 UAC,值得再试一次)。</summary>
    public static bool RelaunchKnownIneffective()
    {
        try
        {
            if (!System.IO.File.Exists(RelaunchIneffectiveFile)) return false;
            var age = DateTime.Now - System.IO.File.GetLastWriteTime(RelaunchIneffectiveFile);
            return age >= TimeSpan.Zero && age < TimeSpan.FromDays(7);
        }
        catch { return false; }
    }

    /// <summary>记下「这台的降权重启无效」,并写清判据(账户名 / 两次运行都是管理员)。</summary>
    public static void MarkRelaunchIneffective(string why)
    {
        try
        {
            var path = RelaunchIneffectiveFile;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var account = "";
            try { account = Environment.UserName; } catch { }
            System.IO.File.WriteAllText(path,
                $"时间:{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}"
                + $"账户:{account}{Environment.NewLine}"
                + $"原因:{why}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>降权成功了(现在是普通权限)—— 清掉旧记录,以后照常走自动降权。</summary>
    public static void ClearIneffectiveMark()
    {
        try { if (System.IO.File.Exists(RelaunchIneffectiveFile)) System.IO.File.Delete(RelaunchIneffectiveFile); } catch { }
    }

    /// <summary>用普通用户权限重新拉起自己。返回是否成功发起(发起成功后调用方应立刻退出本进程)。</summary>
    public static bool TryRelaunchNormal()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        // 【必须绝对路径】CreateProcessAsUser 的 lpApplicationName **不会**去 PATH 里找 ——
        // 传相对/裸文件名会直接 ERROR_FILE_NOT_FOUND(2)。实测验证时就在这踩过一次 ✗,这里做一道保险。
        try { exe = System.IO.Path.GetFullPath(exe); } catch { }

        // ① 首选:把自己的令牌降成中完整性再启动 —— 对内建 Administrator 账户同样有效
        //    (explorer 也是 High 时,拿它的令牌只会得到另一个 High 进程,这正是之前无效的原因)。
        if (TryRelaunchAsMediumIntegrity()) return true;

        // ② 次选:用「外壳(shell=资源管理器)的令牌」创建进程 —— 安装程序 runasoriginaluser 用的同一套办法。
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
    // ---- 完整性级别(2026-11-09:内建 Administrator 账户也能用拖放) ----
    private const int TokenIntegrityLevel = 25;          // TOKEN_INFORMATION_CLASS.TokenIntegrityLevel
    private const int TokenQueryInfo = 0x0008;
    private const uint SE_GROUP_INTEGRITY = 0x00000020;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const int SECURITY_MANDATORY_MEDIUM_RID = 0x2000;
    private const int SECURITY_MANDATORY_HIGH_RID = 0x3000;

    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint STILL_ACTIVE = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tok, int cls, IntPtr buf, int len, out int ret);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr tok, int cls, IntPtr info, int len);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSidToSidW(string sid, out IntPtr sidPtr);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr strPtr);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? app, string cmd,
        uint creationFlags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

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
