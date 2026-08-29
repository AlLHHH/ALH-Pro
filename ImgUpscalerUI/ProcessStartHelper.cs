using System;
using System.Diagnostics;

namespace ALHPro;

public static class ProcessStartHelper
{
    public static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 忽略打开失败
        }
    }

    public static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    /// <summary>打开输出所在文件夹并让文件高亮;单文件用 explorer /select 选中它,多文件打开文件夹
    /// (资源管理器无法一次高亮多个,弹窗会列出文件名对照)。</summary>
    public static void OpenSelect(System.Collections.Generic.IReadOnlyList<string> files)
    {
        if (files is null || files.Count == 0) return;
        var dir = System.IO.Path.GetDirectoryName(files[0]) ?? "";
        if (files.Count == 1)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{files[0]}\"") { UseShellExecute = true });
                return;
            }
            catch { /* 退化为打开文件夹 */ }
        }
        OpenFolder(dir);
    }
}
