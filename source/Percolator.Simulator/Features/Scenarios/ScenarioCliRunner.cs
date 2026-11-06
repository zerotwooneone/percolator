using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Scenarios;

internal static class ScenarioCliRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        Console.WriteLine("[ScenarioCliRunner] Initializing headless scenario runner...");

        var replayIndex = Array.IndexOf(args, "--replay");
        if (replayIndex == -1 || replayIndex >= args.Length - 1)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[Error] Missing scenario path argument after --replay. Example: --replay Scenarios/01-direct-handshake-and-chat.json");
            Console.ResetColor();
            return 1;
        }

        var inputPath = args[replayIndex + 1];
        var resolvedPath = ResolvePath(inputPath);
        var scenarioFiles = new List<string>();

        if (File.Exists(resolvedPath))
        {
            scenarioFiles.Add(resolvedPath);
        }
        else if (Directory.Exists(resolvedPath))
        {
            scenarioFiles.AddRange(Directory.GetFiles(resolvedPath, "*.json", SearchOption.AllDirectories).OrderBy(f => f));
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[Error] Scenario file or directory not found: '{inputPath}' (evaluated as '{resolvedPath}')");
            Console.ResetColor();
            return 1;
        }

        if (scenarioFiles.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Warning] No scenario JSON files found at path: '{resolvedPath}'");
            Console.ResetColor();
            return 0;
        }

        // Initialize Headless Engine
        var logEngine = new MultiSinkLogEngine();
        var services = new ServiceCollection();
        services.AddSingleton<ISimulatorLogSink>(logEngine);
        services.AddLogging(lb =>
        {
            lb.SetMinimumLevel(LogLevel.Trace);
            lb.AddProvider(new NodeScopedLoggerProvider("CLI", SimulatedNodeId.CreateNew(), logEngine));
        });

        services.AddSingleton<ISimulatorStateBus, SimulatorStateBus>();
        services.AddSingleton<ISimulatedWorldStateService, SimulatedWorldStateService>();
        services.AddSingleton<IStatusToastService, StatusToastService>();
        services.AddSingleton<DirectHandshakeOrchestrator>();
        services.AddSingleton<DirectChatCoordinator>();
        services.AddSingleton<ScenarioRunnerService>();

        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<ScenarioRunnerService>();
        var worldState = provider.GetRequiredService<ISimulatedWorldStateService>();

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var allPassed = true;
        var passedCount = 0;
        var failedCount = 0;

        foreach (var file in scenarioFiles)
        {
            var fileName = Path.GetFileName(file);
            Console.WriteLine($"\n[ScenarioCliRunner] >>> Replaying '{fileName}'...");

            try
            {
                var json = await File.ReadAllTextAsync(file);
                var scenario = JsonSerializer.Deserialize<ScenarioDocument>(json, jsonOptions);
                if (scenario == null)
                {
                    throw new InvalidOperationException($"Failed to deserialize scenario document from '{file}'.");
                }

                await runner.RunScenarioAsync(scenario);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[ScenarioCliRunner] ✔ PASS: '{scenario.Name}' ({fileName})");
                Console.ResetColor();
                passedCount++;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ScenarioCliRunner] ✖ FAIL: '{fileName}' - {ex.Message}");
                Console.ResetColor();
                allPassed = false;
                failedCount++;
            }
        }

        await worldState.ClearAllAsync();
        logEngine.Dispose();

        Console.WriteLine("\n==========================================");
        Console.WriteLine($" Replay Summary: Total: {scenarioFiles.Count}, Passed: {passedCount}, Failed: {failedCount}");
        Console.WriteLine("==========================================");

        return allPassed ? 0 : 1;
    }

    private static string ResolvePath(string rawPath)
    {
        if (File.Exists(rawPath) || Directory.Exists(rawPath))
        {
            return rawPath;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, rawPath),
            Path.Combine(Directory.GetCurrentDirectory(), "Percolator.Simulator", rawPath),
            Path.Combine(AppContext.BaseDirectory, "Scenarios", Path.GetFileName(rawPath)),
            Path.Combine(Directory.GetCurrentDirectory(), "Percolator.Simulator", "Scenarios", Path.GetFileName(rawPath))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return rawPath;
    }
}
