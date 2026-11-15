using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace ALHPro
{
    /// <summary>【2026-11-15】给「拖不进来」补的第三条路:老式窗口消息拖放(WM_DROPFILES)。
    ///
    /// 背景:用户反馈「很多人设备根本不能拖放进来,一部分又可以」。WinUI 3 的拖放走 OLE,
    /// 只要程序是提权运行的,UIPI 就会把整条路掐断(没有 API 可以放开 OLE)。
    /// 但 Windows 还留着一条老路:WM_DROPFILES。它**可以**通过
    /// <c>ChangeWindowMessageFilterEx(MSGFLT_ALLOW)</c> 对高完整性窗口放行,
    /// 也能接住那些用老式拖放源的壳(压缩软件、部分下载器、老旧资源管理器替代品)。
    ///
    /// 所以这里做两件事:
    ///   1) 给主窗口挂上 <c>DragAcceptFiles</c>,并放开 WM_DROPFILES / WM_COPYDATA 的消息过滤;
    ///   2) 用 <c>SetWindowSubclass</c> 接住 WM_DROPFILES,把 CF_HDROP 里的路径转成普通路径数组交出去。
    /// 拿到路径之后跟 OLE 拖放走同一条导入流程,用户看不出差别。</summary>
    internal static class ShellDrop
    {
        private const int WM_DROPFILES = 0x0233;
        private const int WM_COPYDATA = 0x004A;
        private const int WM_COPYGLOBALDATA = 0x0049;
        private const uint MSGFLT_ALLOW = 1;

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

        [DllImport("shell32.dll")]
        private static extern void DragFinish(IntPtr hDrop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr changeFilterStruct);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

        // 委托必须一直活着:被 GC 掉之后子类化回调会指向已释放的 thunk,窗口消息直接崩。
        private static SubclassProc? _proc;

        /// <summary>老式拖放拿到文件时触发(在 UI 线程上)。</summary>
        public static event Action<string[]>? FilesDropped;

        /// <summary>给窗口装上老式拖放接收。可以重复调用,内部只装一次。</summary>
        public static void Attach(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _proc is not null) return;
            try
            {
                DragAcceptFiles(hwnd, true);
                // 提权时这两条消息默认被 UIPI 拦掉;放行之后老式拖放仍能进来。
                ChangeWindowMessageFilterEx(hwnd, WM_DROPFILES, MSGFLT_ALLOW, IntPtr.Zero);
                ChangeWindowMessageFilterEx(hwnd, WM_COPYDATA, MSGFLT_ALLOW, IntPtr.Zero);
                ChangeWindowMessageFilterEx(hwnd, WM_COPYGLOBALDATA, MSGFLT_ALLOW, IntPtr.Zero);

                _proc = WndProc;
                bool ok = SetWindowSubclass(hwnd, _proc, (UIntPtr)0x414C48, UIntPtr.Zero);
                AppLogger.Info($"老式拖放(WM_DROPFILES)已就绪 —— 句柄=0x{hwnd.ToInt64():X},子类化={(ok ? "成功" : "失败")}");
            }
            catch (Exception ex)
            {
                AppLogger.Error("装老式拖放接收失败", ex);
            }
        }

        private static IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uId, UIntPtr dwRef)
        {
            if (uMsg == WM_DROPFILES)
            {
                try
                {
                    var paths = ReadDropFiles(wParam);
                    if (paths.Length > 0)
                    {
                        AppLogger.Info($"老式拖放:收到 {paths.Length} 项(OLE 那条路没进来,这条进来了)");
                        FilesDropped?.Invoke(paths);
                    }
                    else
                    {
                        AppLogger.Warn("老式拖放:消息到了但解析不出路径(拖放源给的数据不完整),已忽略");
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("处理老式拖放失败", ex);
                }
                finally
                {
                    try { DragFinish(wParam); } catch { }
                }
                return IntPtr.Zero;
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        /// <summary>把 HDROP 句柄里的文件列表读出来(路径已是完整路径)。</summary>
        private static string[] ReadDropFiles(IntPtr hDrop)
        {
            var list = new List<string>();
            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint len = DragQueryFile(hDrop, i, null, 0);
                if (len == 0) continue;
                var sb = new StringBuilder((int)len + 1);
                if (DragQueryFile(hDrop, i, sb, (uint)sb.Capacity) > 0) list.Add(sb.ToString());
            }
            return list.ToArray();
        }
    }
}