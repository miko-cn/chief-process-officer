namespace Cpo.Service;

/// <summary>门卫管道：判定对端进程是否为可信的 Cpo.App 客户端。</summary>
public interface ITrustedClientValidator
{
    /// <summary>校验 PID 对应进程是否为可信 App。任何异常一律视为不可信（fail-closed）。</summary>
    bool IsTrustedClient(int pid);
}

/// <summary>
/// 生产实现：进程存活 + 可执行文件完整路径校验——必须是 Cpo.App.exe 且位于
/// 开发输出（winapp run 的 AppX）或发布安装目录。发布版可叠加 Authenticode 签名校验。
/// 每次校验都在同一时刻完成"存活 + 路径"复核，压缩 PID 复用（TOCTOU）窗口。
/// </summary>
public sealed class TrustedClientValidator : ITrustedClientValidator
{
    public bool IsTrustedClient(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            var path = process.MainModule?.FileName;
            return !string.IsNullOrEmpty(path) && IsTrustedAppPath(path);
        }
        catch (Exception)
        {
            // 进程不存在 / 权限不足 / 刚退出（TOCTOU）→ 拒绝
            return false;
        }
    }

    private static bool IsTrustedAppPath(string path)
    {
        var normalized = path.Replace('/', '\\');
        // 开发态：winapp run 打包输出 bin\...\AppX\Cpo.App.exe；发布态：安装目录（SPEC §6 分发：Inno Setup）
        return normalized.EndsWith("Cpo.App.exe", StringComparison.OrdinalIgnoreCase)
            && (normalized.Contains("\\Cpo.App\\bin\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\ChiefProcessOfficer\\", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("\\CPO\\", StringComparison.OrdinalIgnoreCase));
    }
}
