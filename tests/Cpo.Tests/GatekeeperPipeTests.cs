using System.IO.Pipes;
using Cpo.Service;
using Xunit;

namespace Cpo.Tests;

/// <summary>
/// 门卫管道集成测试：真实 named pipe 握手 → 对端进程校验（委托注入）→ 会话令牌发放。
/// 校验器用委托替身（真实路径校验在 TrustedClientValidator 单测外另验）；
/// 管道名唯一，避免与运行中 service 的 cpo-gate-&lt;user&gt; 冲突。
/// 禁用并行：与 gRPC 测试同集合（共享管道命名空间）。
/// </summary>
[Collection("NonParallelGrpc")]
public class GatekeeperPipeTests : IAsyncLifetime
{
    private const string PipeName = "cpo-test-gate-pipe";
    private readonly SessionTokenStore _tokens = new();
    private CancellationTokenSource? _cts;
    private Task? _gateTask;

    public Task InitializeAsync()
    {
        _cts = new CancellationTokenSource();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts?.Cancel();
        if (_gateTask is not null)
        {
            try
            {
                await _gateTask;
            }
            catch
            {
                // 忽略取消/关闭异常
            }
        }

        _cts?.Dispose();
    }

    private void StartGate(Func<int, bool> validator)
    {
        var gate = new GatekeeperPipe(_tokens, new DelegateValidator(validator), PipeName);
        _gateTask = gate.RunAsync(_cts!.Token);
    }

    /// <summary>模拟 App 侧握手：连接门卫管道，读一行（空行 = 拒绝）。</summary>
    private static async Task<string?> RequestTokenAsync()
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        using var reader = new StreamReader(pipe);
        var line = await reader.ReadLineAsync();
        return string.IsNullOrEmpty(line) ? null : line;
    }

    [Fact]
    public async Task TrustedClient_ReceivesValidSessionToken()
    {
        StartGate(_ => true);

        var token = await RequestTokenAsync();

        Assert.False(string.IsNullOrEmpty(token));
        Assert.True(_tokens.Validate(token!));
    }

    [Fact]
    public async Task UntrustedClient_ReceivesNoToken()
    {
        StartGate(_ => false);

        var token = await RequestTokenAsync();

        Assert.Null(token);
    }

    [Fact]
    public void SessionToken_ExpiresAfterLifetime()
    {
        var store = new SessionTokenStore(TimeSpan.FromMilliseconds(50));
        var token = store.Issue();

        Assert.True(store.Validate(token));
        Thread.Sleep(120);
        Assert.False(store.Validate(token));
    }

    private sealed class DelegateValidator(Func<int, bool> check) : ITrustedClientValidator
    {
        public bool IsTrustedClient(int pid) => check(pid);
    }
}
