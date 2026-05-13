using Desktop.Wpf.Features.Simulator.Models;
using System.Threading.Channels;

namespace Desktop.Wpf.Features.Simulator;

/// <summary>
/// Wraps an inner ISimulatorStateRepository to serialize all IO operations through a single-writer queue.
/// This ensures at most one file write is in-flight at a time and prevents .tmp file lock contention.
/// </summary>
public sealed class QueuedSimulatorStateRepository : ISimulatorStateRepository, IDisposable
{
    private readonly ISimulatorStateRepository _inner;
    private readonly Channel<Operation> _queue;
    private readonly CancellationTokenSource _cts;
    private readonly Task _consumer;

    public QueuedSimulatorStateRepository(ISimulatorStateRepository inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        
        // Unbounded channel for simplicity; in practice, saves are debounced upstream
        _queue = Channel.CreateUnbounded<Operation>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _cts = new CancellationTokenSource();
        _consumer = Task.Run(() => ConsumerLoopAsync(_cts.Token));
    }

    public async Task<SimulatorStateSnapshot> LoadStateAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<SimulatorStateSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new Operation(OperationType.Load, Snapshot: null, LoadCompletion: tcs, SaveCompletion: null);
        
        if (!_queue.Writer.TryWrite(operation))
        {
            throw new InvalidOperationException("Failed to enqueue load operation.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        try
        {
            return await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(QueuedSimulatorStateRepository));
        }
    }

    public async Task SaveStateAsync(SimulatorStateSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new Operation(OperationType.Save, Snapshot: snapshot, LoadCompletion: null, SaveCompletion: tcs);
        
        if (!_queue.Writer.TryWrite(operation))
        {
            throw new InvalidOperationException("Failed to enqueue save operation.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        try
        {
            await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(QueuedSimulatorStateRepository));
        }
    }

    private async Task ConsumerLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var operation in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                switch (operation.Type)
                {
                    case OperationType.Load:
                        var result = await _inner.LoadStateAsync(cancellationToken).ConfigureAwait(false);
                        operation.LoadCompletion?.TrySetResult(result);
                        break;
                    
                    case OperationType.Save:
                        await _inner.SaveStateAsync(operation.Snapshot!, cancellationToken).ConfigureAwait(false);
                        operation.SaveCompletion?.TrySetResult(true);
                        break;
                }
            }
            catch (Exception ex)
            {
                operation.LoadCompletion?.TrySetException(ex);
                operation.SaveCompletion?.TrySetException(ex);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.Complete();
        try
        {
            _consumer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException) when (_consumer.IsCanceled)
        {
            // Expected on cancellation
        }
        _cts.Dispose();
    }

    private enum OperationType { Load, Save }

    private sealed record Operation(
        OperationType Type,
        SimulatorStateSnapshot? Snapshot,
        TaskCompletionSource<SimulatorStateSnapshot>? LoadCompletion,
        TaskCompletionSource<bool>? SaveCompletion);
}
