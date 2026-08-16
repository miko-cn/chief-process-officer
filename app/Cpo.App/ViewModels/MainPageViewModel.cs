using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cpo.Contracts.Telemetry;
using Cpo.Core.Telemetry;
using Grpc.Net.Client;

namespace Cpo.App.ViewModels;

/// <summary>事件流列表项（UI 展示模型）。</summary>
public sealed record EventRow(string Time, string Type, string Summary);

/// <summary>
/// 主页面 ViewModel（M3）：操作日志审阅面板。
/// 数据源 = gRPC over named pipes（service 端），不再直读 SQLite（根治打包虚拟化路径问题）。
/// 动态刷新：每 2s 拉取最新事件，新事件插入列表顶部（最新在最上面）。
/// 事件信封 payload_json 复用 schema JSON 契约，反序列化后走同一 ToRow 展示逻辑。
/// </summary>
public partial class MainPageViewModel : ObservableObject, IDisposable
{
    private readonly string _pipeName;
    private TelemetryService.TelemetryServiceClient? _client;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private long _lastTsMs;
    private bool _connected;

    public MainPageViewModel(string? pipeName = null)
    {
        // service 端管道名：cpo-telemetry-<用户名>
        _pipeName = pipeName ?? $"cpo-telemetry-{Environment.UserName}";
    }

    [ObservableProperty]
    private string _statusText = "未连接";

    [ObservableProperty]
    private string _serviceInfo = "";

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<EventRow> Events { get; } = new();

    /// <summary>启动动态刷新（每 2s 拉取最新事件，插入列表顶部）。</summary>
    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _client = CreateClient();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        try
        {
            await RefreshStatusAsync();
            await PollAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"连接 service 失败: {ex.Message}（service 未运行？）";
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (await _timer.WaitForNextTickAsync(_cts.Token))
                {
                    await PollAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _timer?.Dispose();
    }

    private TelemetryService.TelemetryServiceClient CreateClient()
    {
        // gRPC over named pipes 官方客户端模式（.NET 8）
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, _) =>
            {
                var stream = new System.IO.Pipes.NamedPipeClientStream(
                    ".", _pipeName, System.IO.Pipes.PipeDirection.InOut,
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

    private async Task RefreshStatusAsync()
    {
        var status = await _client!.GetStatusAsync(new GetStatusRequest());
        ServiceInfo = $"引擎: {status.EngineMode} | samples: {status.SamplesCount:N0} | 日志: {status.EventLogCount:N0}";
    }

    /// <summary>拉取最新事件（倒序，Limit 20），新事件插入顶部。</summary>
    private async Task PollAsync()
    {
        var response = await _client!.QueryEventsAsync(new QueryEventsRequest
        {
            TypePrefix = "policy.",   // 操作日志面板：只看决策/动作/规则变更
            Descending = true,
            Limit = 20,
        });

        // 新事件（ts > 上次看到的最新 ts）插到顶部
        var newest = response.Events.FirstOrDefault();
        if (newest is not null)
        {
            _lastTsMs = Math.Max(_lastTsMs, newest.TsMs);
        }

        var inserted = 0;
        foreach (var envelope in response.Events)
        {
            if (envelope.TsMs <= _lastTsMs - 1 && inserted >= 20)
            {
                break;
            }

            if (Events.Any(e => e.Time == FormatTime(envelope.TsMs) && e.Type == envelope.Type
                                 && e.Summary == Summarize(envelope)))
            {
                continue;
            }

            Events.Insert(0, ToRow(envelope));
            inserted++;
        }

        // 截断到 200 条，防止无限增长
        while (Events.Count > 200)
        {
            Events.RemoveAt(Events.Count - 1);
        }

        StatusText = $"已连接 · 最近 {Events.Count} 条操作记录（每 2s 自动刷新）";
        _connected = true;
    }

    private static EventRow ToRow(TelemetryEventEnvelope envelope) => new(
        FormatTime(envelope.TsMs),
        envelope.Type,
        Summarize(envelope));

    private static string FormatTime(long tsMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(tsMs).ToLocalTime().ToString("HH:mm:ss.fff");

    private static string Summarize(TelemetryEventEnvelope envelope)
    {
        // payload_json 是 schema camelCase 契约；按类型直接反序列化为事件模型再摘要
        var evt = TelemetryEventSerializer.Deserialize(envelope.Type, envelope.PayloadJson);
        return evt switch
        {
            PolicyDecisionEvent d => $"建议: {d.TargetName} → {ActionText(d.ConclusionJson)}（{d.Trigger}）",
            PolicyActionEvent a => $"{ActionKindText(a.Kind)}: {a.TargetName} → {ResultText(a)}",
            RuleChangedEvent r => $"规则 {r.RuleId}: {r.ChangeKind}（{r.Source}）",
            _ => evt.ToString() ?? envelope.Type,
        };
    }

    private static string ActionText(string conclusionJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(conclusionJson);
            var action = doc.RootElement.GetProperty("action").GetString();
            return action ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private static string ActionKindText(ActionKind kind) => kind switch
    {
        ActionKind.SetPriority => "降优先级",
        ActionKind.SetAffinity => "限制亲和性",
        ActionKind.Throttle => "限流",
        ActionKind.Restore => "恢复原值",
        _ => kind.ToString(),
    };

    private static string ResultText(PolicyActionEvent a) => a.Result switch
    {
        ActionResult.Succeeded => a.Error is null ? "成功" : $"成功（{a.Error}）",
        ActionResult.Failed => $"失败（{a.Error}）",
        _ => a.Result.ToString(),
    };
}
