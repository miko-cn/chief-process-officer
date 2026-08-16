using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cpo.Contracts.Telemetry;
using Cpo.Core.Telemetry;
using Grpc.Core;
using Grpc.Net.Client;

namespace Cpo.App.ViewModels;

/// <summary>事件流列表项（UI 展示模型）。Key = 内容键（基于原始 ts_ms，事件不可变 → 稳定，供增量 diff）。</summary>
public sealed record EventRow(long TsMs, string Time, string Type, string Summary)
{
    /// <summary>内容键：ts_ms|type|summary。用于判断两轮拉取之间的差异（相同 = 不重绘）。
    /// 用原始毫秒而非显示格式——显示只到秒，若用显示格式，同一秒内同类型同摘要事件会 Key 撞车。</summary>
    public string Key => $"{TsMs}|{Type}|{Summary}";
}

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
    private bool _connected;

    /// <summary>会话令牌（门卫管道经对端进程校验后发放，只存内存不落盘）。null = 未握手/已失效。</summary>
    private string? _sessionToken;

    /// <summary>前台监听（SetWinEventHook，UI 线程回调；SPEC §6 前台检测归属定案）。</summary>
    private readonly Cpo.App.Native.ForegroundWatcher _foregroundWatcher = new();

    /// <summary>最近一次前台（pid, name）：service 重启（令牌重取）后补报用。</summary>
    private (int Pid, string Name)? _lastForeground;

    /// <summary>
    /// 干预开关同步标志：程序侧（GetStatus 刷新/失败回滚）同步 IsInterventionEnabled 时置位，
    /// 防止 TwoWay 绑定把程序同步误判为用户操作而触发 gRPC 调用（死循环）。
    /// </summary>
    private bool _syncingIntervention;

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

    /// <summary>ProBalance 开关（服务端状态镜像）。TwoWay 绑定 ToggleSwitch；用户切换触发 gRPC 调用。</summary>
    [ObservableProperty]
    private bool _isInterventionEnabled;

    /// <summary>
    /// 程序侧同步开关状态（GetStatus 刷新 / 调用失败回滚）。置同步标志，不触发用户操作路径。
    /// </summary>
    public void SyncInterventionEnabled(bool enabled)
    {
        _syncingIntervention = true;
        try
        {
            IsInterventionEnabled = enabled;
        }
        finally
        {
            _syncingIntervention = false;
        }
    }

    /// <summary>
    /// 用户切换开关（ToggleSwitch TwoWay 绑定触发）→ 调 gRPC 控制面。
    /// 失败回滚 UI 并提示；Unauthenticated 时清令牌（下一轮自动重握手）。
    /// </summary>
    partial void OnIsInterventionEnabledChanged(bool value)
    {
        if (_syncingIntervention)
        {
            return;
        }

        _ = ApplyInterventionAsync(value);
    }

    private async Task ApplyInterventionAsync(bool enabled)
    {
        try
        {
            var status = await _client!.SetInterventionEnabledAsync(
                new SetInterventionEnabledRequest { Enabled = enabled }, WithAuth());
            SyncInterventionEnabled(status.InterventionEnabled);
            ServiceInfo = BuildServiceInfo(status);
            StatusText = $"已连接 · ProBalance {(status.InterventionEnabled ? "开" : "关")}";
        }
        catch (Exception ex)
        {
            SyncInterventionEnabled(!enabled);   // 回滚到原状态
            if (ex is Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.Unauthenticated })
            {
                _sessionToken = null;
            }

            StatusText = $"开关切换失败: {ex.Message}";
        }
    }

    public ObservableCollection<EventRow> Events { get; } = new();

    /// <summary>启动动态刷新（每 2s 拉取最新事件，插入列表顶部）。</summary>
    public async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _client = CreateClient();
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        // 前台监听：hook 事件上报 + 启动时立即上报当前前台（service 侧启发式的前台保护输入）
        _foregroundWatcher.ForegroundChanged += OnForegroundChanged;
        _foregroundWatcher.Start();

        try
        {
            // 首次握手后立即补报前台（hook 启动时可能早于令牌就绪，上报被静默丢弃）
            if (await EnsureSessionAsync() && _lastForeground is { } fg)
            {
                await ReportForegroundAsync(fg.Pid, fg.Name);
            }

            await RefreshStatusAsync();
            await PollAsync();
        }
        catch (Exception ex)
        {
            // 初次连接失败不退出：进入下方轮询循环自动重试（service 可能稍后启动/重启）
            StatusText = $"连接 service 失败: {ex.Message}（service 未运行？重试中…）";
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
                        if (ex is Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.Unauthenticated })
                        {
                            // 会话令牌失效（service 重启等）→ 清空，下一轮经门卫重新握手
                            _sessionToken = null;
                        }

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
        _foregroundWatcher.ForegroundChanged -= OnForegroundChanged;
        _foregroundWatcher.Dispose();
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

    /// <summary>
    /// 确保持有有效会话令牌：无令牌时先经门卫管道握手（service 侧校验对端进程必须是 Cpo.App.exe）。
    /// 返回是否**新获取**了令牌（true = 刚握手，调用方应补报前台等状态）。
    /// </summary>
    private async Task<bool> EnsureSessionAsync(CancellationToken ct = default)
    {
        if (_sessionToken is not null)
        {
            return false;
        }

        _sessionToken = await RequestSessionTokenAsync(ct);
        return true;
    }

    /// <summary>前台变化（hook 回调，UI 线程）：记录 + 上报 service（失败静默，Poll 补报兜底）。</summary>
    private void OnForegroundChanged(int pid, string name)
    {
        _lastForeground = (pid, name);
        _ = ReportForegroundAsync(pid, name);
    }

    private async Task ReportForegroundAsync(int pid, string name)
    {
        try
        {
            await _client!.ReportForegroundAsync(
                new ForegroundReportRequest { Pid = pid, Name = name }, WithAuth());
        }
        catch
        {
            // 静默：service 未起/令牌未就绪时忽略，PollAsync 每次新握手后补报
        }
    }

    /// <summary>门卫握手：连接 cpo-gate-&lt;user&gt;，经对端进程校验后领取会话令牌（内存态，不落盘）。</summary>
    private static async Task<string> RequestSessionTokenAsync(CancellationToken ct)
    {
        var gateName = $"cpo-gate-{Environment.UserName}";
        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", gateName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TimeSpan.FromSeconds(5), ct);
        using var reader = new System.IO.StreamReader(pipe);
        var token = (await reader.ReadLineAsync(ct))?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("门卫拒绝握手（客户端进程不受信任？）");
        }

        return token;
    }

    /// <summary>携带会话令牌的调用选项（与 service 端 AuthInterceptor 对应）。</summary>
    private CallOptions WithAuth(CancellationToken ct = default)
    {
        var headers = new Metadata();
        if (_sessionToken is not null)
        {
            headers.Add("cpo-auth-token", _sessionToken);
        }

        return new CallOptions(headers, cancellationToken: ct);
    }

    private async Task RefreshStatusAsync()
    {
        var status = await _client!.GetStatusAsync(new GetStatusRequest(), WithAuth());
        SyncInterventionEnabled(status.InterventionEnabled);
        ServiceInfo = BuildServiceInfo(status);
    }

    private static string BuildServiceInfo(ServiceStatus status) =>
        $"引擎: {status.EngineMode} | ProBalance: {(status.InterventionEnabled ? "开" : "关")} | samples: {status.SamplesCount:N0} | 日志: {status.EventLogCount:N0}";

    /// <summary>拉取最新事件（倒序，Limit 20），与现有列表做增量合并：无变化 → 零操作（不闪烁）。</summary>
    private async Task PollAsync()
    {
        // 新握手（service 重启后令牌失效重取）→ 补报前台，保证启发式前台保护持续有效
        if (await EnsureSessionAsync() && _lastForeground is { } fg)
        {
            await ReportForegroundAsync(fg.Pid, fg.Name);
        }

        var response = await _client!.QueryEventsAsync(new QueryEventsRequest
        {
            TypePrefix = "policy.",   // 操作日志面板：只看决策/动作/规则变更
            Descending = true,        // 最新在前
            Limit = 20,
        }, WithAuth());

        // 响应为倒序（Events[0] = 最新）→ rows 与现有列表同序，可直接按位 diff
        var rows = new List<EventRow>(response.Events.Count);
        foreach (var envelope in response.Events)
        {
            rows.Add(ToRow(envelope));
        }

        var lastRefresh = DateTimeOffset.Now.ToLocalTime().ToString("HH:mm:ss");
        // ObservableCollection 只能在 UI 线程修改 → marshal 回 UI（后台轮询线程不可直接改）
        _uiDispatcher.TryEnqueue(() =>
        {
            MergeRows(rows);
            StatusText = $"已连接 · 最近 {Events.Count} 条操作记录（每 2s 自动刷新 · 最后刷新 {lastRefresh}）";
        });
        _connected = true;
    }

    /// <summary>
    /// 增量合并：rows 是权威顺序（最新在前）。无差异 → 不动集合（解决全量重建闪烁）；
    /// 有差异 → 只增删变化项，滚动位置与可见项尽量保持。
    /// </summary>
    private void MergeRows(List<EventRow> rows)
    {
        // 快速路径：内容完全一致 → 零操作（不触发任何 UI 重绘）
        if (rows.Count == Events.Count)
        {
            var same = true;
            for (var i = 0; i < rows.Count; i++)
            {
                if (Events[i].Key != rows[i].Key) { same = false; break; }
            }

            if (same) return;
        }

        var incoming = new HashSet<string>(rows.Count);
        foreach (var row in rows)
        {
            incoming.Add(row.Key);
        }

        // 1) 删：现有中已不在最新窗口内的项（新事件把旧事件挤出 Limit 上限）
        for (var i = Events.Count - 1; i >= 0; i--)
        {
            if (!incoming.Contains(Events[i].Key))
            {
                Events.RemoveAt(i);
            }
        }

        // 2) 增/对齐：逐位检查，缺失处插入（保持最新在前）
        for (var i = 0; i < rows.Count; i++)
        {
            if (i < Events.Count && Events[i].Key == rows[i].Key) continue;
            Events.Insert(i, rows[i]);
        }

        // 3) 收尾：残留超长项删除（防御，正常已被 1 覆盖）
        while (Events.Count > rows.Count)
        {
            Events.RemoveAt(Events.Count - 1);
        }
    }

    private static EventRow ToRow(TelemetryEventEnvelope envelope) => new(
        envelope.TsMs,
        FormatTime(envelope.TsMs),
        envelope.Type,
        Summarize(envelope));

    private static string FormatTime(long tsMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(tsMs).ToLocalTime().ToString("HH:mm:ss");

    private static string Summarize(TelemetryEventEnvelope envelope)
    {
        // payload_json 是 schema camelCase 契约；按类型直接反序列化为事件模型再摘要
        var evt = TelemetryEventSerializer.Deserialize(envelope.Type, envelope.PayloadJson);
        return evt switch
        {
            PolicyDecisionEvent d => $"建议: {d.TargetName} → {ActionText(d.ConclusionJson)}（{d.Trigger}）",
            PolicyActionEvent a => $"{ActionKindText(a.Kind)}: {a.TargetName} → {ResultText(a)}",
            RuleChangedEvent r => $"规则 {r.RuleId}: {r.ChangeKind}（{r.Source}）",
            InterventionToggledEvent t => $"ProBalance 开关: {(t.Enabled ? "开启" : "关闭")}（{t.Source}）",
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
