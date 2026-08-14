using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cpo.Core.Storage;
using Cpo.Core.Telemetry;

namespace Cpo.App.ViewModels;

/// <summary>事件流列表项（UI 展示模型）。</summary>
public sealed record EventRow(string Time, string Type, string Summary);

/// <summary>
/// 主页面 ViewModel（M1 壳）：显示遥测录制状态与最近事件流。
/// M1 直接读取本地 SQLite（验证数据链路）；M2 改为经 gRPC over named pipes 从服务订阅。
/// </summary>
public partial class MainPageViewModel : ObservableObject
{
    private readonly string _dbPath;

    public MainPageViewModel(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cpo", "telemetry.db");
    }

    [ObservableProperty]
    private string _databasePath = "";

    [ObservableProperty]
    private string _statusText = "未加载";

    [ObservableProperty]
    private string _eventCountText = "";

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<EventRow> Events { get; } = new();

    public async Task InitializeAsync()
    {
        DatabasePath = _dbPath;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await using var store = new SqliteTelemetryStore(_dbPath);
            await store.InitializeAsync();

            var count = await store.CountAsync();
            EventCountText = $"事件总数: {count:N0}";

            var byType = new Dictionary<string, long>();
            Events.Clear();
            var shown = 0;

            await foreach (var evt in store.QueryAsync(new EventQuery { Limit = 200 }))
            {
                byType[evt.Type] = byType.GetValueOrDefault(evt.Type) + 1;
                if (shown < 100)
                {
                    Events.Add(ToRow(evt));
                    shown++;
                }
            }

            var breakdown = string.Join("  ", byType
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            StatusText = $"最近 100 条事件已加载（共 {count:N0}）。{breakdown}";
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败: {ex.Message}（服务未运行？数据库: {_dbPath}）";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static EventRow ToRow(TelemetryEvent evt)
    {
        var time = DateTimeOffset.FromUnixTimeMilliseconds(evt.TsMs).ToLocalTime().ToString("HH:mm:ss.fff");
        var summary = evt switch
        {
            ProcessLifecycleEvent lc => $"{lc.Kind} {lc.Name} (pid {lc.Pid}, ppid {lc.Ppid})",
            CpuSampleEvent cpu when cpu.Scope == SampleScope.Process => $"{cpu.Name} CPU {cpu.CpuPercent:F1}%",
            CpuSampleEvent cpu => $"系统 CPU {cpu.CpuPercent:F1}% ({cpu.CoreCount} 核)",
            MemorySampleEvent mem when mem.Scope == SampleScope.Process => $"{mem.Name} WS {FormatBytes(mem.WorkingSetBytes)}",
            MemorySampleEvent mem => $"系统内存 可用 {FormatBytes(mem.AvailableBytes)} / {FormatBytes(mem.TotalBytes)}",
            _ => evt.ToString() ?? evt.Type,
        };
        return new EventRow(time, evt.Type, summary);
    }

    private static string FormatBytes(long? bytes) =>
        bytes is long b && b > 0
            ? b >= 1_073_741_824 ? $"{b / 1_073_741_824.0:F1} GB" : $"{b / 1_048_576.0:F1} MB"
            : "-";
}
