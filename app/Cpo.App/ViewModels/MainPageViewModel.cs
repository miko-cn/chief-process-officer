using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cpo.Contracts.Telemetry;
using Cpo.Core.Telemetry;
using Grpc.Core;
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
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _uiDispatcher;
    private TelemetryService.TelemetryServiceClient? _client;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private long _lastTsMs;
    private bool _connected;

    public MainPageViewModel(string? pipeName = null)
    {
        // service 端管道名：cpo-telemetry-<用户名>
        _pipeName = pipeName ?? $"cpo-telemetry-{Environment.UserName}";
        // ViewModel 在 UI 线程构造：捕获 DispatcherQueue，后台轮询结果 marshal 回 UI
        // （WinUI ObservableCollection 只能在 UI 线程修改）
        _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                        ?? throw new InvalidOperationException("MainPageViewModel 必须在 UI 线程构造");
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
                    try
                    {
                        await PollAsync();
                    }
                    catch (Exception ex)
                    {
                        // 轮询异常不杀任务：记录后继续下一轮（service 可能瞬时不可用）
                        _uiDispatcher.TryEnqueue(() =>
                            StatusText = $"刷新失败: {ex.Message}（重试中…）");
                    }
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
        var client = new TelemetryService.TelemetryServiceClient(channel);
        _ = client; // 每次调用通过 CallOptions 携带令牌（见下方 WithAuth）
        return client;
    }

    /// <summary>携带连接令牌的调用选项（与 service 端 AuthInterceptor 对应）。</summary>
    private static CallOptions WithAuth(CancellationToken ct = default)
    {
        var headers = new Metadata();
        var token = ReadToken();
        if (token is not null)
        {
            headers.Add("cpo-auth-token", token);
        }

        return new CallOptions(headers, cancellationToken: ct);
    }

    private static string? ReadToken()
    {
        try
        {
            // service 生成的令牌文件：%PROGRAMDATA%\Cpo\auth-token（打包 app 可读——文件 ACL 含当前用户）
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Cpo", "auth-token");
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task RefreshStatusAsync()
    {
        var status = await _client!.GetStatusAsync(new GetStatusRequest(), WithAuth());
        ServiceInfo = $"引擎: {status.EngineMode} | samples: {status.SamplesCount:N0} | 日志: {status.EventLogCount:N0}";
    }

    /// <summary>拉取最新事件（倒序，Limit 20），每轮全量重建列表，最新在最上面。</summary>
    private async Task PollAsync()
    {
        var response = await _client!.QueryEventsAsync(new QueryEventsRequest
        {
            TypePrefix = "policy.",   // 操作日志面板：只看决策/动作/规则变更
            Descending = true,        // 最新在前
            Limit = 20,
        }, WithAuth());

        // 响应为倒序（Events[0] = 最新）→ 直接按序重建，列表索引 0 = 最新在最上面。
        // 全量重建（Limit 20）天然有序、无重复，避免 Insert(0) 造成的逆序 bug。
        var rows = new List<EventRow>(response.Events.Count);
        foreach (var envelope in response.Events)
        {
            rows.Add(ToRow(envelope));
        }

        if (response.Events.Count > 0)
        {
            _lastTsMs = response.Events[0].TsMs;
        }

        var lastRefresh = DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss");
        // ObservableCollection 只能在 UI 线程修改 → marshal 回 UI（后台轮询线程不可直接改）
        _uiDispatcher.TryEnqueue(() =>
        {
            Events.Clear();
            foreach (var row in rows)
            {
                Events.Add(row);
            }

            StatusText = $"已连接 · 最近 {Events.Count} 条操作记录（每 2s 自动刷新 · 最后刷新 {lastRefresh}）";
        });
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
