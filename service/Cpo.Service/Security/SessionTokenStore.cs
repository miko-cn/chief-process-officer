using System.Collections.Concurrent;

namespace Cpo.Service;

/// <summary>
/// 会话令牌存储（M3 安全第 3 层落地）：门卫管道签发 → gRPC 拦截器校验。
/// 令牌只存内存、短期有效（默认 12h）、不落盘；service 重启即全部失效。
/// 文件令牌已废弃：同用户可读，无法作为有效凭据（见 SPEC §6 安全基线）。
/// </summary>
public sealed class SessionTokenStore
{
    private readonly ConcurrentDictionary<string, long> _tokens = new(); // token → 过期时间（Unix 毫秒）
    private readonly TimeSpan _lifetime;

    public SessionTokenStore(TimeSpan? lifetime = null)
    {
        _lifetime = lifetime ?? TimeSpan.FromHours(12);
    }

    /// <summary>签发新会话令牌（256-bit 随机）。</summary>
    public string Issue()
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        _tokens[token] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)_lifetime.TotalMilliseconds;
        return token;
    }

    /// <summary>校验令牌有效（存在且未过期）；过期项惰性清理。</summary>
    public bool Validate(string token)
    {
        if (!_tokens.TryGetValue(token, out var expiresMs))
        {
            return false;
        }

        if (expiresMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            return true;
        }

        _tokens.TryRemove(token, out _);
        return false;
    }
}
