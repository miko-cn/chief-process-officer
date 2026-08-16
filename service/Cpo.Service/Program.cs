using Cpo.Core.Engine;
using Cpo.Core.Rules;
using Cpo.Core.Sampling;
using Cpo.Core.Storage;
using Cpo.Core.Telemetry;
using Cpo.Interop;

namespace Cpo.Service;

/// <summary>
/// CPO 遥测服务宿主（M2）：周期采集本机负载轨迹 → SQLite；策略引擎评估 → 决策日志；
/// 自动模式下经执行路径干预并自动恢复。
/// M2 仍以控制台宿主运行（管理员权限），Windows 服务形态 M2 后期/M3。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = LoadOptions(args);
        var config = options.Config;
        var dbPath = options.DatabasePath;

        Console.WriteLine("CPO 遥测服务 (M2)");
        Console.WriteLine($"  数据库: {dbPath}");
        Console.WriteLine($"  系统采样: {config.SystemSampleIntervalMs}ms | 保留: {(int)TimeSpan.FromMilliseconds(config.RetentionMs).TotalDays} 天");
        Console.WriteLine($"  引擎模式: {(options.Mode == DecisionMode.Automatic ? "自动（执行干预）" : "监督（仅建议，不执行）")}");
        if (options.RuleFile is not null)
        {
            Console.WriteLine($"  规则文件: {options.RuleFile}");
        }

        Console.WriteLine("  按 Ctrl+C 停止。");

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        await using var store = new SqliteTelemetryStore(dbPath);
        await using var recorder = new TelemetryRecorder(store, config);

        // 规则集：文件加载（可缺省）+ 演示规则
        var rules = new RuleStore();
        if (options.RuleFile is not null && File.Exists(options.RuleFile))
        {
            rules.LoadFromFile(options.RuleFile);
        }
        else
        {
            // 内置演示规则：编译器/工具风暴降为 BelowNormal（监督模式只建议，安全）
            rules.Add(new PolicyRule
            {
                Id = "demo.build-tools",
                ProcessPattern = "*build*",
                Action = RuleActionKind.SetPriority,
                PriorityClass = ProcessController.PriorityBelowNormal,
                Source = RuleChangeSource.User,
            });
        }

        // 策略运行器：引擎 + 执行路径 + 决策日志
        var runner = new PolicyRunner(store, ProcessController.CreateController(), rules, Environment.ProcessorCount)
        {
            Mode = options.Mode,
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n正在停止…");
            cts.Cancel();
        };

        try
        {
            // 采集与策略评估并行：采集写库，策略每周期评估一次
            var evaluateTask = EvaluateLoopAsync(runner, config.SystemSampleIntervalMs, cts.Token);
            await recorder.RunAsync(cts.Token);
            await evaluateTask;
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }

        var restored = runner.Shutdown();
        var count = await store.CountAsync();
        Console.WriteLine($"已停止。事件总数: {count}，恢复干预: {restored}");
        return 0;
    }

    private static async Task EvaluateLoopAsync(PolicyRunner runner, int intervalMs, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(500, intervalMs)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await runner.EvaluateAsync(ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PolicyRunner] 评估异常: {ex}");
            }
        }
    }

    private sealed record ServiceOptions(
        SamplingConfig Config,
        string DatabasePath,
        DecisionMode Mode,
        string? RuleFile);

    private static ServiceOptions LoadOptions(string[] args)
    {
        var config = new SamplingConfig();
        var mode = DecisionMode.Supervised;
        string? ruleFile = null;

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
                case "--engine" when parts[1] is "auto" or "automatic":
                    mode = DecisionMode.Automatic;
                    break;
                case "--rules":
                    ruleFile = parts[1];
                    break;
            }
        }

        var env = Environment.GetEnvironmentVariable("CPO_DB_PATH");
        var dbPath = !string.IsNullOrEmpty(env)
            ? env
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cpo", "telemetry.db");

        return new ServiceOptions(config, dbPath, mode, ruleFile);
    }
}
