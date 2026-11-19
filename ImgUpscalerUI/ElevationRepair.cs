using System;
using System.Diagnostics;
using Microsoft.Win32;
using ALHPro;

namespace ImgUpscalerUI
{
    /// <summary>
    /// 把「以管理员身份运行此程序」这个兼容性勾选清掉。
    ///
    /// 【背景 · 2026-11-19】管理员权限下 Windows 会掐掉资源管理器拖进来的文件(UIPI),
    /// 拖放事件根本不进窗口,用户看到的就是一个红叉。UIPI 没办法绕,只能保证自己
    /// 别用管理员权限跑:启动时先降权重开(App.xaml.cs 那一套),顺带把用户手工勾上的
    /// `RUNASADMIN` 兼容性标记删掉,否则下次启动又回到管理员模式。
    /// </summary>
    internal static class ElevationRepair
    {
        private const string LayersSubKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

        /// <summary>当前进程的 exe 路径;取不到返回 null(此时不做任何事)。</summary>
        public static string? CurrentExePath()
        {
            try
            {
                return Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch (Exception ex)
            {
                AppLogger.Info($"兼容性标记:读不出当前 exe 路径 —— {ex.Message}");
                return null;
            }
        }

        /// <summary>Layers 里给这个 exe 记的兼容性标记;没有则返回 null。</summary>
        public static string? ReadFlags(string exePath)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(LayersSubKey, writable: false);
                if (key is null) return null;
                foreach (var name in key.GetValueNames())
                {
                    if (!string.Equals(name, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                    return key.GetValue(name) as string;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Info($"兼容性标记:读取失败 —— {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// 清掉 RUNASADMIN。返回 true 表示确实改动过(调用方据此写日志/提示)。
        /// 写注册表可能被安全软件拦,失败只记日志、绝不抛出 —— 这不是关键路径。
        /// </summary>
        public static bool TryClearRunAsAdminFlag()
        {
            var exePath = CurrentExePath();
            if (string.IsNullOrEmpty(exePath)) return false;

            var flags = ReadFlags(exePath);
            if (!CompatLayerFlags.ContainsRunAsAdmin(flags)) return false;

            var remaining = CompatLayerFlags.RemoveRunAsAdmin(flags);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(LayersSubKey, writable: true);
                if (key is null) return false;

                if (remaining is null)
                {
                    key.DeleteValue(exePath, throwOnMissingValue: false);
                    AppLogger.Info($"兼容性标记:已删除「以管理员身份运行此程序」(原值 `{flags}`)—— 管理员权限下 Windows 会拦掉拖放");
                }
                else
                {
                    key.SetValue(exePath, remaining, RegistryValueKind.String);
                    AppLogger.Info($"兼容性标记:已去掉 RUNASADMIN,保留 `{remaining}`(原值 `{flags}`)—— 管理员权限下 Windows 会拦掉拖放");
                }
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Info($"兼容性标记:清除失败(不影响使用,拖放仍受权限限制)—— {ex.Message}");
                return false;
            }
        }
    }
}