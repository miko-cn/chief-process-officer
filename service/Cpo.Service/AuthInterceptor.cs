using System.Security.AccessControl;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Cpo.Service;

/// <summary>认证配置：连接令牌。</summary>
public sealed record AuthOptions(string Token);

/// <summary>
/// gRPC 认证拦截器：校验每个请求 metadata 中的连接令牌（防同用户任意进程直接调用）。
/// 令牌由 service 启动时生成/加载（%PROGRAMDATA%\Cpo\auth-token，SYSTEM 权限），
/// app 读取同一文件后经 metadata 携带。
/// </summary>
public sealed class AuthInterceptor : Interceptor
{
    private readonly AuthOptions _options;

    public AuthInterceptor(AuthOptions options)
    {
        _options = options;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        EnsureAuthenticated(context);
        return await continuation(request, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        EnsureAuthenticated(context);
        await continuation(request, responseStream, context);
    }

    private void EnsureAuthenticated(ServerCallContext context)
    {
        var provided = context.RequestHeaders
            .FirstOrDefault(h => h.Key == AuthConstants.TokenHeaderKey)?.Value;

        if (provided is null || !CryptographicEquals(provided, _options.Token))
        {
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "无效或缺失连接令牌（仅 Cpo.App 可访问）"));
        }
    }

    /// <summary>恒定时间比较，避免时序侧信道。</summary>
    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }
}

/// <summary>认证常量。</summary>
public static class AuthConstants
{
    /// <summary>gRPC metadata 令牌头名。</summary>
    public const string TokenHeaderKey = "cpo-auth-token";

    /// <summary>令牌文件路径（%PROGRAMDATA%\Cpo\auth-token）。</summary>
    public static string TokenFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Cpo", "auth-token");
}

/// <summary>连接令牌管理（service 侧：生成/加载）。</summary>
public static class AuthTokenManager
{
    private const int TokenLengthBytes = 32; // 256-bit

    /// <summary>
    /// 加载或生成令牌。首次运行生成随机 256-bit 令牌并写入
    /// %PROGRAMDATA%\Cpo\auth-token（ACL 限制为 SYSTEM + 当前用户）。
    /// 返回令牌字符串。
    /// </summary>
    public static string LoadOrCreate()
    {
        var path = AuthConstants.TokenFilePath;
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 32)
            {
                return existing;
            }
        }

        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(TokenLengthBytes));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, token);

        // ACL：SYSTEM + 当前用户完全控制，其余拒绝（管理员可读——app 以普通用户运行需读，故含当前用户）
        try
        {
            var security = new FileSecurity(path, System.Security.AccessControl.AccessControlSections.All);
            security.SetOwner(System.Security.Principal.WindowsIdentity.GetCurrent().User);
            security.AddAccessRule(new FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                FileSystemRights.Read | FileSystemRights.Write,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch
        {
            // ACL 设置失败不致命（默认 ACL 已限当前用户）
        }

        return token;
    }
}
