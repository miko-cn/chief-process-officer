using Cpo.Core.Sampling;
using Cpo.Core.Storage;
using Cpo.Service;

namespace Cpo.Service;

/// <summary>
/// CPO 遥测服务宿主（M1）：周期采集本机负载轨迹 → SQLite。
/// M1 以控制台宿主运行（管理员权限），M2 转为 Windows 服务形态。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var config = LoadConfig(args);
        var dbPath = ResolveDatabasePath();

        Console.WriteLine($"CPO 遥测服务 (M1)");
        Console.WriteLine($"  数据库: {dbPath}");
        Console.WriteLine($"  系统采样: {config.SystemSampleIntervalMs}ms | 进程采样: {config.ProcessSampleIntervalMs}ms | 保留: {(int)TimeSpan.FromMilliseconds(config.RetentionMs).TotalDays} 天");
        Console.WriteLine("  按 Ctrl+C 停止。");

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using var store = new SqliteTelemetryStore(dbPath);
        await using var recorder = new TelemetryRecorder(store, config);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n正在停止…");
            cts.Cancel();
        };

        try
        {
            await recorder.RunAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }

        var count = await store.CountAsync();
        Console.WriteLine($"已停止。事件总数: {count}");
        return 0;
    }

    private static SamplingConfig LoadConfig(string[] args)
    {
        var config = new SamplingConfig();

        // 简易命令行覆盖：--interval-ms=1000 --retention-days=7（M1 够用，正式配置文件 M2 引入）
        foreach (var arg in args)
        {
            var parts = arg.Split('=', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            switch (parts[0])
            {
                case "--interval-ms" when int.TryParse(parts[1], out var interval):
                    config = config with
                    {
                        SystemSampleIntervalMs = interval,
                        ProcessSampleIntervalMs = interval,
                        LifecycleScanIntervalMs = interval,
                    };
                    break;
                case "--retention-days" when double.TryParse(parts[1], out var days):
                    config = config with { RetentionMs = (long)(TimeSpan.FromDays(days).TotalMilliseconds) };
                    break;
            }
        }

        return config;
    }

    private static string ResolveDatabasePath()
    {
        var env = Environment.GetEnvironmentVariable("CPO_DB_PATH");
        if (!string.IsNullOrEmpty(env))
        {
            return env;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cpo", "telemetry.db");
    }
}
