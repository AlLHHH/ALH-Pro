// 测试用 EngineService stub:仅提供生产 CutoutService.cs 所需的 FindCutoutModel。
namespace ALHPro;

public static class EngineService
{
    private static string? _modelDir;

    public static void SetModelDir(string dir) => _modelDir = dir;

    public static string? FindCutoutModel(string fileName)
    {
        if (_modelDir == null) return null;
        var p = Path.Combine(_modelDir, fileName);
        return File.Exists(p) ? p : null;
    }

    public static void RegisterTempFile(string _) { }
}
