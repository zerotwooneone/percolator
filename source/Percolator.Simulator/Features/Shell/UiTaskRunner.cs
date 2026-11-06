using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.Shell;

/// <summary>
/// Standardized background task execution helper for the Simulator UI.
/// Because Microsoft.Data.Sqlite, Signal cryptography, SChannel certificate generation,
/// and DI host bootstrapping execute synchronously even inside async methods, all heavy work
/// triggered by UI commands or state signals must be offloaded to the ThreadPool via <see cref="UiTaskRunner"/>.
/// </summary>
internal static class UiTaskRunner
{
    /// <summary>
    /// Offloads an asynchronous operation to a background ThreadPool thread and returns to the caller's context.
    /// Catches and logs any unhandled exceptions to prevent crashing the UI dispatcher.
    /// </summary>
    public static async Task RunAsync(
        Func<Task> backgroundWork,
        string category,
        ISimulatorLogSink? logSink = null,
        IStatusToastService? toastService = null,
        string? nodeName = null,
        SimulatedNodeId? nodeId = null)
    {
        try
        {
            await Task.Run(backgroundWork);
        }
        catch (OperationCanceledException)
        {
            // Ignore cancellation
        }
        catch (Exception ex)
        {
            logSink?.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                nodeName,
                nodeId,
                category,
                $"Background task failed: {ex.Message}",
                ex.ToString()));

            if (toastService is not null)
            {
                PostToUi(() => toastService.ShowToast($"{category} Error", ex.Message, ToastSeverity.Error));
            }
        }
    }

    /// <summary>
    /// Offloads an asynchronous operation with a result to a background ThreadPool thread.
    /// </summary>
    public static async Task<T?> RunAsync<T>(
        Func<Task<T>> backgroundWork,
        string category,
        ISimulatorLogSink? logSink = null,
        IStatusToastService? toastService = null,
        string? nodeName = null,
        SimulatedNodeId? nodeId = null)
    {
        try
        {
            return await Task.Run(backgroundWork);
        }
        catch (OperationCanceledException)
        {
            return default;
        }
        catch (Exception ex)
        {
            logSink?.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                nodeName,
                nodeId,
                category,
                $"Background task failed: {ex.Message}",
                ex.ToString()));

            if (toastService is not null)
            {
                PostToUi(() => toastService.ShowToast($"{category} Error", ex.Message, ToastSeverity.Error));
            }

            return default;
        }
    }

    /// <summary>
    /// Fires a background task without awaiting it on the UI thread, logging any unhandled exceptions.
    /// </summary>
    public static void FireAndForget(
        Func<Task> backgroundWork,
        string category,
        ISimulatorLogSink? logSink = null,
        IStatusToastService? toastService = null,
        string? nodeName = null,
        SimulatedNodeId? nodeId = null)
    {
        _ = RunAsync(backgroundWork, category, logSink, toastService, nodeName, nodeId);
    }

    /// <summary>
    /// Marshals an action to the Avalonia UI thread (executes inline if already on the UI thread).
    /// </summary>
    public static void PostToUi(Action uiAction)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            uiAction();
        }
        else
        {
            Dispatcher.UIThread.Post(uiAction);
        }
    }
}
