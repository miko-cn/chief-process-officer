using Cpo.Contracts.Telemetry;
using Cpo.Core.Storage;
using Cpo.Core.Telemetry;
using Cpo.Service;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cpo.Tests;

/// <summary>
/// gRPC over named pipes 集成测试：启动真实服务器 → 客户端经管道查询/订阅。
/// 禁用并行：SqliteTelemetryStore.DisposeAsync 会 ClearAllPools，影响其他测试类的共享内存库。
/// </summary>
[Collection("NonParallelGrpc")]
public class GrpcNamedPipeTests : IAsyncLifetime
{
    private const string PipeName = "cpo-test-pipe";
    private SessionTokenStore _tokens = null!;
    private SqliteTelemetryStore _store = null!;
    private WebApplication _server = null!;

    public async Task InitializeAsync()
    {
        _store = SqliteTelemetryStore.CreateInMemory();
        await _store.InitializeAsync();

        _tokens = new SessionTokenStore();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions());
        builder.Services.AddSingleton<ITelemetryStore>(_store);
        builder.Services.AddSingleton<Func<DecisionMode>>(() => DecisionMode.Automatic);
        builder.Services.AddSingleton<TelemetryGrpcService>();
        builder.Services.AddSingleton(_tokens);
        builder.Services.AddGrpc(o => o.Interceptors.Add<AuthInterceptor>());
        builder.WebHost.UseNamedPipes(o => o.CurrentUserOnly = true);
        builder.WebHost.ConfigureKestrel(k =>
            k.ListenNamedPipe(PipeName, listenOptions => listenOptions.Protocols =
                Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
        _server = builder.Build();
        _server.MapGrpcService<TelemetryGrpcService>();
        await _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
        // 注意：不调 SqliteTelemetryStore.DisposeAsync（其 ClearAllPools 会清全局池，
        // 影响并行测试类的共享内存库）。内存库随进程结束回收。
    }

    /// <summary>签发自用会话令牌（生产路径是门卫管道签发；测试直接注入存储）。</summary>
    private string IssueToken() => _tokens.Issue();

    private TelemetryService.TelemetryServiceClient CreateClient()
    {
        // gRPC over named pipes 官方客户端模式（.NET 8）：
        // GrpcChannel.ForAddress + SocketsHttpHandler.ConnectCallback 返回 NamedPipeClientStream
        // 参考: https://learn.microsoft.com/aspnet/core/grpc/interprocess-namedpipes
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, _) =>
            {
                var stream = new System.IO.Pipes.NamedPipeClientStream(
                    ".", PipeName, System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous);
                await stream.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                return stream;
            },
        };
        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
        return new TelemetryService.TelemetryServiceClient(channel);
    }

    private static CallOptions AuthCall(string? token)
    {
        var headers = new Metadata();
        if (token is not null)
        {
            headers.Add(AuthConstants.TokenHeaderKey, token);
        }

        return new CallOptions(headers);
    }

    [Fact]
    public async Task Unauthenticated_Call_IsRejected()
    {
        await _store.AppendBatchAsync(new TelemetryEvent[]
        {
            new PolicyDecisionEvent(200, "cpu.storm", 42, "msbuild.exe", "[]", "{}", DecisionMode.Automatic, "{}"),
        });

        var client = CreateClient();

        // 无令牌 → Unauthenticated
        var ex = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
            await client.GetStatusAsync(new GetStatusRequest(), AuthCall(token: null)));
        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, ex.StatusCode);

        // 错误令牌 → Unauthenticated
        var ex2 = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () =>
            await client.QueryEventsAsync(new QueryEventsRequest(), AuthCall("wrong-token")));
        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, ex2.StatusCode);
    }

    [Fact]
    public async Task QueryEvents_RoundTripsEvents()
    {
        await _store.AppendBatchAsync(new TelemetryEvent[]
        {
            new CpuSampleEvent(100, SampleScope.System, null, null, 12.5, 100, 8, 1000),
            new PolicyDecisionEvent(200, "cpu.storm", 42, "msbuild.exe", "[]", "{}", DecisionMode.Automatic, "{}"),
        });

        var client = CreateClient();
        var response = await client.QueryEventsAsync(new QueryEventsRequest
        {
            TypePrefix = "policy.",
        }, AuthCall(IssueToken()));

        var envelope = Assert.Single(response.Events);
        Assert.Equal(TelemetryEventTypes.PolicyDecision, envelope.Type);
        Assert.Equal(200, envelope.TsMs);
        Assert.Contains("\"targetPid\":42", envelope.PayloadJson);   // schema JSON 契约原样
    }

    [Fact]
    public async Task GetStatus_ReportsCountsAndMode()
    {
        await _store.AppendBatchAsync(new TelemetryEvent[]
        {
            new CpuSampleEvent(100, SampleScope.System, null, null, 1, 1, 8, 1000),
            new PolicyDecisionEvent(200, "cpu.storm", 42, "a.exe", "[]", "{}", DecisionMode.Automatic, "{}"),
        });

        var client = CreateClient();
        var status = await client.GetStatusAsync(new GetStatusRequest(), AuthCall(IssueToken()));

        Assert.Equal("automatic", status.EngineMode);
        Assert.Equal(1, status.SamplesCount);
        Assert.Equal(1, status.EventLogCount);
    }

    [Fact]
    public async Task QueryEvents_Descending_ReturnsLatestFirst()
    {
        await _store.AppendBatchAsync(new TelemetryEvent[]
        {
            new PolicyDecisionEvent(100, "t1", 1, "a.exe", "[]", "{}", DecisionMode.Automatic, "{}"),
            new PolicyDecisionEvent(200, "t2", 2, "b.exe", "[]", "{}", DecisionMode.Automatic, "{}"),
            new PolicyDecisionEvent(300, "t3", 3, "c.exe", "[]", "{}", DecisionMode.Automatic, "{}"),
        });

        var client = CreateClient();
        var response = await client.QueryEventsAsync(new QueryEventsRequest
        {
            TypePrefix = "policy.",
            Descending = true,
            Limit = 2,
        }, AuthCall(IssueToken()));

        Assert.Equal(2, response.Events.Count);
        Assert.Equal(300, response.Events[0].TsMs);
        Assert.Equal(200, response.Events[1].TsMs);
    }
}
