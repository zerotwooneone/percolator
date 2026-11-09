using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.DiscoverySimulation;
using Percolator.Simulator.Features.GroupMessaging;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.SwarmSimulation;
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
        services.AddSingleton<IVirtualClockStateService, VirtualClockStateService>();
        services.AddSingleton<IFlightRecorderStateService, FlightRecorderStateService>();
        services.AddSingleton<RichChatActionCoordinator>();
        services.AddSingleton<ProfileSyncCoordinator>();
        services.AddSingleton<ISimulatedNodeFactory, SimulatedNodeFactory>();
        services.AddSingleton<DirectHandshakeOrchestrator>();
        services.AddSingleton<DirectChatCoordinator>();
        services.AddSingleton<RelayProvisioningCoordinator>();
        services.AddSingleton<GroupChatCoordinator>();
        services.AddSingleton<DiscoverySimulationCoordinator>();
        services.AddSingleton<SyntheticFilePresetGenerator>();
        services.AddSingleton<SwarmSimulationCoordinator>();
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

        Console.WriteLine($"[ScenarioCliRunner] Found {scenarioFiles.Count} scenario(s) to execute.");

        for (int i = 0; i < scenarioFiles.Count; i++)
        {
            var filePath = scenarioFiles[i];
            var fileName = Path.GetFileName(filePath);
            Console.WriteLine($"------------------------------------------------------------");
            Console.WriteLine($"[{i + 1}/{scenarioFiles.Count}] Loading '{fileName}'...");

            try
            {
                var jsonContent = await File.ReadAllTextAsync(filePath);
                var scenario = JsonSerializer.Deserialize<ScenarioDocument>(jsonContent, jsonOptions);

                if (scenario == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[FAIL] Could not deserialize '{fileName}' into a ScenarioDocument.");
                    Console.ResetColor();
                    allPassed = false;
                    failedCount++;
                    continue;
                }

                // Reset world state between scenarios
                worldState.ClearAll();

                await runner.RunScenarioAsync(scenario);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"[PASS] Scenario '{scenario.Name}' passed successfully ({scenario.Steps.Count} steps, {scenario.Assertions.Count} assertions).");
                Console.ResetColor();
                passedCount++;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] Scenario '{fileName}' failed with exception:");
                Console.WriteLine($"       {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"       Inner: {ex.InnerException.Message}");
                }
                Console.ResetColor();
                allPassed = false;
                failedCount++;
            }
        }

        Console.WriteLine($"============================================================");
        if (allPassed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[ScenarioCliRunner] ALL {passedCount} SCENARIOS PASSED.");
            Console.ResetColor();
            return 0;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ScenarioCliRunner] {failedCount} OF {scenarioFiles.Count} SCENARIO(S) FAILED. ({passedCount} passed)");
            Console.ResetColor();
            return 1;
        }
    }

    private static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var candidate1 = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(candidate1) || Directory.Exists(candidate1))
        {
            return candidate1;
        }

        var candidate2 = Path.GetFullPath(path);
        return candidate2;
    }
}
