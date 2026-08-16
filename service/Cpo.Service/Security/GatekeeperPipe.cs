using System.IO.Pipes;
using Cpo.Interop;

namespace Cpo.Service;

/// <summary>
/// 门卫管道（M3 安全第 3 层落地）：独立 raw named pipe，App 启动/断线时来握手。
/// 流程：App 连接 → GetNamedPipeClientProcessId 取对端 PID → TrustedClientValidator 校验
/// （必须是 Cpo.App.exe）→ 通过则经 SessionTokenStore 发放内存会话令牌（不落盘）。
/// 效果：即使任何磁盘文件被同用户恶意进程读到，它也拿不到会话令牌 → 调不动 gRPC。
/// </summary>
public sealed class GatekeeperPipe
{
    public const string PipePrefix = "cpo-gate-";

    private readonly SessionTokenStore _tokens;
    private readonly ITrustedClientValidator _validator;
    private readonly string _pipeName;

    /// <param name="pipeName">管道名（测试注入唯一名，避免与运行中 service 的管道冲突）。</param>
    public GatekeeperPipe(SessionTokenStore tokens, ITrustedClientValidator validator, string? pipeName = null)
    {
        _tokens = tokens;
        _validator = validator;
        _pipeName = pipeName ?? PipePrefix + Environment.UserName;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"  门卫管道: \\\\.\\pipe\\{_pipeName}（对端进程校验 → 发放会话令牌）");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);

                var clientPid = ProcessController.GetClientProcessId(server.SafePipeHandle);
                if (clientPid is int pid && _validator.IsTrustedClient(pid))
                {
                    var token = _tokens.Issue();
                    await using (var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true })
                    {
                        await writer.WriteLineAsync(token);
                    }

                    Console.WriteLine($"[Gate] 发放会话令牌 → pid {pid}");
                }
                else
                {
                    // 拒绝：写空行（客户端据此判定握手失败），不发放令牌
                    Console.WriteLine($"[Gate] 拒绝未知客户端 pid={clientPid?.ToString() ?? "?"}");
                    await using (var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true })
                    {
                        await writer.WriteLineAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Gate] 处理异常: {ex.Message}");
            }
        }
    }
}
