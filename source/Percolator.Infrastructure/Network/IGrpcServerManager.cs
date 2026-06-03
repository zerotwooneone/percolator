using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.Infrastructure.Network;

public interface IGrpcServerManager
{
    Task<ServerStartResult> StartAsync(SelfId selfId, ListeningPort port, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task RestartAsync(SelfId selfId, ListeningPort port, CancellationToken ct);
}

public class ServerStartResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsPortConflict { get; init; }
    
    public static ServerStartResult Succeeded() => new() { Success = true };
    public static ServerStartResult Failed(string error, bool isPortConflict = false) 
        => new() { Success = false, ErrorMessage = error, IsPortConflict = isPortConflict };
}
