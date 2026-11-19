using System;
using System.Collections.Generic;

namespace ALHPro
{
    /// <summary>
    /// HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers 里那串兼容性标记。
    ///
    /// 【为什么需要它 · 2026-11-19】用户反馈「拖视频进去只有一个红叉,进不来」。
    /// 除了「虚拟文件没有本地路径」那类之外,还有一个纯属自己捅的坑:用户在
    /// exe 属性 → 兼容性 里勾了「以管理员身份运行此程序」,Windows 就把这串标记写进
    /// Layers 键(形如 `~ RUNASADMIN`),于是程序每次都是高完整性级别启动,
    /// 资源管理器把文件往我们窗口上拖时被 UIPI 直接掐断 —— 光标变禁止符,拖放事件根本不进来。
    /// UIPI 没有 API 可绕,唯一出路就是别用管理员权限跑,所以启动时把这串标记清掉。
    ///
    /// 只做纯字符串处理,注册表读写留给调用方(便于单测)。
    /// </summary>
    public static class CompatLayerFlags
    {
        public const string RunAsAdmin = "RUNASADMIN";

        /// <summary>这串标记里有没有 RUNASADMIN(大小写不敏感,兼容 `~RUNASADMIN` 这种没空格的写法)。</summary>
        public static bool ContainsRunAsAdmin(string? layerValue)
        {
            foreach (var token in Tokenize(layerValue))
            {
                if (IsRunAsAdminToken(token)) return true;
            }
            return false;
        }

        /// <summary>
        /// 去掉 RUNASADMIN,保留其它标记(HIGHDPIAWARE、WIN8RTM 之类一律原样留着)。
        /// 返回值:剩下的标记串;返回 null 表示「整条值都该删掉」(只剩 RUNASADMIN 一个标记)。
        /// </summary>
        public static string? RemoveRunAsAdmin(string? layerValue)
        {
            var tokens = Tokenize(layerValue);
            if (tokens.Count == 0) return null;

            var kept = new List<string>(tokens.Count);
            foreach (var token in tokens)
            {
                if (IsRunAsAdminToken(token)) continue;
                kept.Add(token);
            }

            if (kept.Count == 0) return null;
            // 只剩 `~` 前缀也没有意义,一并当作"整条删掉"
            if (kept.Count == 1 && kept[0] == "~") return null;
            return string.Join(" ", kept);
        }

        private static List<string> Tokenize(string? layerValue)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(layerValue)) return list;
            foreach (var raw in layerValue!.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                list.Add(raw);
            }
            return list;
        }

        private static bool IsRunAsAdminToken(string token)
        {
            var t = token.TrimStart('~');
            return string.Equals(t, RunAsAdmin, StringComparison.OrdinalIgnoreCase);
        }
    }
}