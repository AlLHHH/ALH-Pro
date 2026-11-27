using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AlhPro
{
    /// <summary>【2026-11-15】拖进来的东西怎么变成文件列表。
    ///
    /// 用户原话:「SVFI 那种拖入视频什么的 无论什么情况都可以拖进去 我们就不行」;
    /// 后来纠正是「很多人设备根本不能拖放进来」,所以本类只管「路径 → 文件列表」这一小步,
    /// 真正的修复在三条与 OLE 无关的入口(见 ShellDrop.cs / 命令行 / Ctrl+V)。
    /// 这里的规矩很小:
    ///   · 文件 → 原样收下(不按后缀拦:能不能处理交给后面的探测去说,2026-11-12 定过);
    ///   · 文件夹 → **默认不收**(用户 2026-11-15 明确「文件夹现在也收???我要的不是这个」);
    ///     需要递归时显式传 includeFolders,并受深度与条数封顶保护。</summary>
    public static class DroppedPaths
    {
        /// <summary>视频页认的后缀(与「添加视频」的文件选择器保持一致)。</summary>
        public static readonly string[] VideoExtensions =
        {
            ".mp4", ".mkv", ".mov", ".webm", ".avi", ".m4v",
            ".ts", ".m2ts", ".flv", ".wmv", ".mpg", ".mpeg", ".rmvb", ".3gp",
        };

        /// <summary>图片页认的后缀。</summary>
        public static readonly string[] ImageExtensions =
        {
            ".png", ".jpg", ".jpeg", ".jfif", ".webp", ".bmp", ".tif", ".tiff", ".gif", ".avif",
            ".heic", ".heif",
        };

        /// <summary>纯音频后缀(音频页用来分流;视频后缀另算 —— 视频拖到音频页会当场提取音轨)。
        /// 【2026-11-27】原来窗口级拖放只认「视频 / 图片」两类,mp3 拖到音频页被当成非法文件,
        /// 混合拖放里的 mp3 也会无提示消失。</summary>
        public static readonly string[] AudioExtensions =
        {
            ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aif", ".aiff", ".ape",
        };

        /// <summary>一次最多收多少个文件:够用,且不会因为拖进一个大目录而假死。</summary>
        public const int MaxFiles = 2000;

        /// <summary>文件夹往下最多找几层。</summary>
        public const int MaxDepth = 6;

        /// <summary>把「拖进来的路径」摊成「要导入的文件」。</summary>
        /// <param name="paths">拖进来的每一项(文件或文件夹的完整路径)。</param>
        /// <param name="images">true = 图片页(按图片后缀筛),false = 视频页。</param>
        /// <param name="includeFolders">true 才把文件夹递归摊开;默认 false —— 拖文件夹不导入,只提示。</param>
        public static string[] Expand(IEnumerable<string> paths, bool images, int maxFiles = MaxFiles, bool includeFolders = false)
        {
            if (paths is null) return Array.Empty<string>();
            var exts = images ? ImageExtensions : VideoExtensions;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();

            foreach (var raw in paths)
            {
                if (list.Count >= maxFiles) break;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string p;
                try { p = Path.GetFullPath(raw); } catch { continue; }

                if (File.Exists(p))
                {
                    // 单个文件不按后缀拦:先收下,处理不了的时候由探测给出具体原因。
                    if (seen.Add(p)) list.Add(p);
                    continue;
                }

                if (!Directory.Exists(p)) continue;
                if (!includeFolders) continue;   // 默认不摊文件夹(2026-11-15 用户要求)

                foreach (var f in EnumerateMedia(p, exts, maxFiles - list.Count))
                {
                    if (seen.Add(f)) list.Add(f);
                    if (list.Count >= maxFiles) break;
                }
            }

            return list.ToArray();
        }

        private static IEnumerable<string> EnumerateMedia(string root, string[] exts, int budget)
        {
            if (budget <= 0) yield break;
            var found = 0;

            // 先按层摊(广度优先):用户把「一堆子文件夹」一起拖进来时,先拿到浅层的素材,
            // 而不是在第一层某个巨大的子目录里把额度用光。
            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((root, 0));

            while (queue.Count > 0)
            {
                var (dir, depth) = queue.Dequeue();
                string[] files;
                try { files = Directory.GetFiles(dir); } catch { files = Array.Empty<string>(); }
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                foreach (var f in files)
                {
                    if (found >= budget) yield break;
                    var ext = Path.GetExtension(f);
                    if (exts.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        found++;
                        yield return f;
                    }
                }

                if (depth >= MaxDepth) continue;
                string[] subs;
                try { subs = Directory.GetDirectories(dir); } catch { subs = Array.Empty<string>(); }
                Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
                foreach (var s in subs) queue.Enqueue((s, depth + 1));
            }
        }
    }
}