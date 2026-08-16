using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Cpo.Service;

/// <summary>
/// gRPC 认证拦截器（M3 安全第 3 层落地）：校验每个请求 metadata 中的**会话令牌**。
/// 会话令牌由门卫管道（GatekeeperPipe）在对端进程校验通过后发放，只存内存、短期有效；
/// 文件令牌已废弃（同用户可读，无法作为有效凭据）。
/// </summary>
public sealed class AuthInterceptor : Interceptor
{
    private readonly SessionTokenStore _tokens;

    public AuthInterceptor(SessionTokenStore tokens)
    {
        _tokens = tokens;
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

        // 令牌为 256-bit 随机值，字典查找的时序差异无可利用价值（无需恒定时间比较）
        if (provided is null || !_tokens.Validate(provided))
        {
            throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "无效或缺失会话令牌（仅经门卫校验的 Cpo.App 可访问）"));
        }
    }
}

/// <summary>认证常量。</summary>
public static class AuthConstants
{
    /// <summary>gRPC metadata 令牌头名。</summary>
    public const string TokenHeaderKey = "cpo-auth-token";
}
