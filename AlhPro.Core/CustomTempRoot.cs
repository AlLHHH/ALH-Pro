using System;

namespace AlhPro.Core
{
    /// <summary>
    /// 【2026-10-17】用户自定义临时目录的准入判定。
    /// 为什么需要它:设置里「临时文件位置」让用户挑**任意**文件夹,挑完这个目录就成了本软件的临时根 ——
    /// 启动清理与「立即清理残留」会去扫它(`alh_*`/`imgup_*` 等前缀),而且免费版和 ALH Pro Max 认同一个根
    /// (见 <see cref="TempCleanup"/> 里 2026-10-15 那次跨版本删活目录的事故)。
    /// 把别人的目录当临时根,轻则把用户的同名文件当残留删掉,重则两版互相删掉正在写的文件。
    /// 所以只放行两种:①**空文件夹**(用户专为临时文件新建的);②**软件自己的目录**
    /// (里面有标记文件 <see cref="MarkerFileName"/>,或本身就是软件新建的专用子目录 <see cref="SuggestedSubDirName"/>)。
    /// 判定是纯函数(不碰文件系统),真正的探测留在界面侧 —— 与 AlhPro.Core 其它纯逻辑文件同一分工。
    /// </summary>
    public static class CustomTempRoot
    {
        /// <summary>软件在专用临时目录里放的标记文件:下次再挑同一个目录(或另一个版本来看)就知道"这目录是本软件在用的"。
        /// 名字用中文,是为了让用户在资源管理器里一眼看懂这目录是谁建的、能不能删。</summary>
        public const string MarkerFileName = "ALHPro临时目录.txt";

        /// <summary>用户挑的目录"已经有别的东西"时,软件在这个目录里新建的专用子目录名
        /// (与自动模式的 `&lt;盘&gt;:\ALHProTemp` 同名,口径一致)。</summary>
        public const string SuggestedSubDirName = "ALHProTemp";

        /// <summary>标记文件里写的内容(纯文本,给人看)。</summary>
        public const string MarkerContent =
            "这个文件夹被 ALH Pro 用作临时文件目录。\r\n" +
            "里面有这个文件就说明它是本软件自己的目录(不是随便挑的用户文件夹)。\r\n" +
            "软件运行时的中间文件会写在这里,任务结束/启动清理时会删掉;这个说明文件可以留着,删了也不影响使用。\r\n";

        /// <summary>准入判定的结果。</summary>
        public enum Verdict
        {
            /// <summary>可以当临时根(空目录,或软件自己的目录)。</summary>
            Ok,
            /// <summary>目录不存在(可能被删了,或者 U 盘/移动硬盘没插)。</summary>
            Missing,
            /// <summary>选的是整个盘根(如 `D:\`):普通权限在盘根写文件会被系统拒绝,不能当临时根。</summary>
            DriveRoot,
            /// <summary>目录里已经有别的文件(不是空的、也没有本软件的标记文件)。</summary>
            NotEmpty,
        }

        /// <summary>
        /// 四项探测结果 → 准入判定。四项都由界面侧量好传进来(纯函数,便于单测):
        /// <paramref name="exists"/> 目录存在吗;<paramref name="isDriveRoot"/> 是不是盘根;
        /// <paramref name="isEmpty"/> 里头一个条目都没有吗(量不出来时按 false 传);
        /// <paramref name="hasMarker"/> 里头有 <see cref="MarkerFileName"/> 吗。
        /// 顺序有意为之:不存在/盘根先拒(和"里面有没有东西"无关),再有标记放行,最后才看空不空。
        /// </summary>
        public static Verdict Check(bool exists, bool isDriveRoot, bool isEmpty, bool hasMarker)
        {
            if (!exists) return Verdict.Missing;
            if (isDriveRoot) return Verdict.DriveRoot;
            if (hasMarker) return Verdict.Ok;          // 软件自己的目录:里头有上次跑剩的临时文件也照收
            return isEmpty ? Verdict.Ok : Verdict.NotEmpty;
        }

        /// <summary>给用户看的一句话(为什么不能选这个位置)。Ok 返回空串。</summary>
        public static string Describe(Verdict v) => v switch
        {
            Verdict.Missing => "这个文件夹不存在(可能已经被删掉,或者 U 盘/移动硬盘没插)。",
            Verdict.DriveRoot => "不能把整个盘(比如 D:\\)当临时目录:普通程序在盘根写文件会被系统拒绝。请选盘里的一个文件夹。",
            Verdict.NotEmpty => $"这个文件夹里已经有别的东西了。临时文件会直接写在这里,清理残留时也会扫这里,"
                              + $"所以只能选空文件夹;或者在它里面新建一个专用子文件夹({SuggestedSubDirName})。",
            _ => "",
        };
    }
}
