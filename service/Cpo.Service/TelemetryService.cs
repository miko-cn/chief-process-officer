using Cpo.Contracts.Telemetry;
using Cpo.Core.Storage;
using Cpo.Core.Telemetry;
using Grpc.Core;
namespace Cpo.Service;

/// <summary>
/// gRPC 遥测服务实现（gRPC over named pipes，本地 IPC）。
/// 数据面：QueryEvents（审阅面板查询）/ WatchEvents（动态刷新推送）/ GetStatus。
/// 控制面：SetInterventionEnabled（ProBalance 开关，会话⑫定案）。
/// 信封 payload_json 直接复用 schema 契约（TelemetryEventSerializer 格式），不重复定义字段。
/// </summary>
public sealed class TelemetryGrpcService : Contracts.Telemetry.TelemetryService.TelemetryServiceBase
{
    private readonly ITelemetryStore _store;
    private readonly PolicyRunner _runner;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public TelemetryGrpcService(ITelemetryStore store, PolicyRunner runner)
    {
        _store = store;
        _runner = runner;
    }

    public override async Task<QueryEventsResponse> QueryEvents(
        QueryEventsRequest request, ServerCallContext context)
    {
        var query = new EventQuery
        {
            FromMs = request.HasFromMs ? request.FromMs : null,
            ToMs = request.HasToMs ? request.ToMs : null,
            Type = string.IsNullOrEmpty(request.Type) ? null : request.Type,
            TypePrefix = string.IsNullOrEmpty(request.TypePrefix) ? null : request.TypePrefix,
            Pid = request.HasPid ? request.Pid : null,
            Limit = request.HasLimit ? request.Limit : null,
            Descending = request.Descending,
            Table = request.Table switch
            {
                QueryEventsRequest.Types.Table.Samples => TelemetryTable.Samples,
                QueryEventsRequest.Types.Table.EventLog => TelemetryTable.EventLog,
                _ => null,
            },
        };

        var response = new QueryEventsResponse();
        await foreach (var evt in _store.QueryAsync(query, context.CancellationToken))
        {
            response.Events.Add(ToEnvelope(evt));
        }

        return response;
    }

    public override async Task WatchEvents(
        WatchEventsRequest request,
        IServerStreamWriter<TelemetryEventEnvelope> responseStream,
        ServerCallContext context)
    {
        // M3 简化：轮询拉取新事件（since_ms 之后）推送给订阅者。
        // 后续可升级为内存广播（引擎评估时同步推给订阅者），避免轮询延迟。
        var sinceMs = request.SinceMs;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));

        while (await timer.WaitForNextTickAsync(context.CancellationToken))
        {
            var query = new EventQuery
            {
                FromMs = sinceMs == 0 ? null : sinceMs,
                TypePrefix = string.IsNullOrEmpty(request.TypePrefix) ? null : request.TypePrefix,
                Limit = 500,
            };

            long lastTs = sinceMs;
            await foreach (var evt in _store.QueryAsync(query, context.CancellationToken))
            {
                await responseStream.WriteAsync(ToEnvelope(evt), context.CancellationToken);
                lastTs = Math.Max(lastTs, evt.TsMs);
            }

            sinceMs = lastTs;
        }
    }

    public override async Task<ServiceStatus> GetStatus(
        GetStatusRequest request, ServerCallContext context)
    {
        return new ServiceStatus
        {
            EngineMode = _runner.Mode == DecisionMode.Automatic ? "automatic" : "supervised",
            StartedAtMs = _startedAt.ToUnixTimeMilliseconds(),
            SamplesCount = await _store.CountAsync(TelemetryTable.Samples, context.CancellationToken),
            EventLogCount = await _store.CountAsync(TelemetryTable.EventLog, context.CancellationToken),
            InterventionEnabled = _runner.InterventionEnabled,
        };
    }

    public override async Task<ServiceStatus> SetInterventionEnabled(
        SetInterventionEnabledRequest request, ServerCallContext context)
    {
        await _runner.SetInterventionEnabledAsync(request.Enabled, "app", context.CancellationToken);
        return await GetStatus(new GetStatusRequest(), context);
    }

    public override async Task<global::Cpo.Contracts.Telemetry.ForegroundReportResponse> ReportForeground(
        ForegroundReportRequest request, ServerCallContext context)
    {
        // 前台状态进入引擎（启发式的前台保护输入）+ 落盘 ui.foreground 事件（schema §4，可审计）
        _runner.ForegroundPid = request.Pid;
        await _store.AppendAsync(new ForegroundEvent(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), request.Pid, request.Name, null),
            context.CancellationToken);
        return new global::Cpo.Contracts.Telemetry.ForegroundReportResponse();
    }

    private static TelemetryEventEnvelope ToEnvelope(TelemetryEvent evt) => new()
    {
        TsMs = evt.TsMs,
        Type = evt.Type,
        PayloadJson = TelemetryEventSerializer.Serialize(evt),
    };
}
