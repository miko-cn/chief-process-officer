using Cpo.Core.Engine;
using Cpo.Core.Rules;
using Cpo.Core.Sampling;
using Cpo.Core.Storage;
using Cpo.Core.Telemetry;
using Cpo.Interop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;
using Microsoft.Extensions.DependencyInjection;

namespace Cpo.Service;

/// <summary>
/// CPO 遥测服务宿主（M3）：周期采集本机负载轨迹 → SQLite 双表分层；
/// 策略引擎评估 → 决策日志；自动模式下经执行路径干预并自动恢复。
/// 分层保留：samples 短保留（1h）+ event_log 长保留（30d），周期清理。
/// M3 仍以控制台宿主运行（管理员权限），Windows 服务形态后续里程碑。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = LoadOptions(args);
        var config = options.Config;
        var dbPath = options.DatabasePath;

        Console.WriteLine("CPO 遥测服务 (M3)");
        Console.WriteLine($"  数据库: {dbPath}");
        Console.WriteLine($"  系统采样: {config.SystemSampleIntervalMs}ms | " +
                          $"samples 保留: {(int)TimeSpan.FromMilliseconds(config.SamplesRetentionMs).TotalMinutes} 分钟 | " +
                          $"event_log 保留: {(int)TimeSpan.FromMilliseconds(config.EventLogRetentionMs).TotalDays} 天");
        Console.WriteLine($"  引擎模式: {(options.Mode == DecisionMode.Automatic ? "自动（执行干预）" : "监督（仅记录，不执行）")}");
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
            // 内置演示规则：编译器/工具风暴降为 BelowNormal（保守）
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

        // gRPC over named pipes（本地 IPC，GUI↔服务通信）
        // 安全加固（2026-08-15）：
        //   1) CurrentUserOnly=true —— 管道 ACL 只允许当前用户连接（防其他用户/服务）
        //   2) 连接令牌校验 —— gRPC metadata 携带共享令牌，拦截器校验（防同用户任意进程直接调用）
        var pipeName = $"cpo-telemetry-{Environment.UserName}";
        var authToken = AuthTokenManager.LoadOrCreate();
        var grpcBuilder = WebApplication.CreateSlimBuilder(new WebApplicationOptions());
        grpcBuilder.WebHost.UseNamedPipes(o => o.CurrentUserOnly = true);
        grpcBuilder.WebHost.ConfigureKestrel(k =>
            k.ListenNamedPipe(pipeName, listenOptions => listenOptions.Protocols =
                Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));
        grpcBuilder.Services.AddSingleton<ITelemetryStore>(store);
        grpcBuilder.Services.AddSingleton<Func<DecisionMode>>(() => runner.Mode);
        grpcBuilder.Services.AddSingleton<TelemetryGrpcService>();
        grpcBuilder.Services.AddGrpc(o =>
        {
            // 所有 RPC 方法统一走令牌校验拦截器
            o.Interceptors.Add<AuthInterceptor>();
        });
        grpcBuilder.Services.AddSingleton(new AuthOptions(authToken));
        var grpcServer = grpcBuilder.Build();
        grpcServer.MapGrpcService<TelemetryGrpcService>();
        await grpcServer.StartAsync(cts.Token);
        Console.WriteLine($"  gRPC: \\\\.\\pipe\\{pipeName}（仅当前用户 + 令牌校验）");

        try
        {
            // 三个并行任务：采集写库 / 策略评估 / 分层清理
            var evaluateTask = EvaluateLoopAsync(runner, config.SystemSampleIntervalMs, cts.Token);
            var purgeTask = PurgeLoopAsync(store, config, cts.Token);
            await recorder.RunAsync(cts.Token);
            await evaluateTask;
            await purgeTask;
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }

        var restored = runner.Shutdown();
        await grpcServer.StopAsync();
        var count = await store.CountAsync();
        var samples = await store.CountAsync(TelemetryTable.Samples);
        var logs = await store.CountAsync(TelemetryTable.EventLog);
        Console.WriteLine($"已停止。samples: {samples} | event_log: {logs} | 恢复干预: {restored}");
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

    /// <summary>
    /// 分层清理循环（schema §8.2）：samples 短保留、event_log 长保留。
    /// </summary>
    private static async Task PurgeLoopAsync(SqliteTelemetryStore store, SamplingConfig config, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(config.PurgeIntervalMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                var removedSamples = await store.PurgeBeforeAsync(
                    TelemetryTable.Samples, nowMs - config.SamplesRetentionMs, ct);
                var removedLogs = await store.PurgeBeforeAsync(
                    TelemetryTable.EventLog, nowMs - config.EventLogRetentionMs, ct);
                Console.WriteLine($"[Purge] samples 清理 {removedSamples} | event_log 清理 {removedLogs}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Purge] 清理异常: {ex.Message}");
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
        var mode = DecisionMode.Automatic;
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
                case "--samples-retention-min" when int.TryParse(parts[1], out var minutes):
                    config = config with { SamplesRetentionMs = (long)TimeSpan.FromMinutes(minutes).TotalMilliseconds };
                    break;
                case "--log-retention-days" when double.TryParse(parts[1], out var days):
                    config = config with { EventLogRetentionMs = (long)TimeSpan.FromDays(days).TotalMilliseconds };
                    break;
                case "--purge-interval-ms" when int.TryParse(parts[1], out var purgeMs):
                    config = config with { PurgeIntervalMs = purgeMs };
                    break;
                case "--engine" when parts[1] is "supervised":
                    mode = DecisionMode.Supervised;
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
