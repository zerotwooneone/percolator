using Grpc.Core;
using Percolator.Contracts.Protos;
using System.Threading.Tasks;

namespace Percolator.Application
{
    public class FileSharingService : FileSharing.FileSharingBase
    {
        // In a real application, a logger would be injected here.

        public override Task<AnnounceManifestResponse> AnnounceManifest(AnnounceManifestRequest request, ServerCallContext context)
        {
            Console.WriteLine($"[gRPC] Received manifest announcement from {context.Peer}.");
            return Task.FromResult(new AnnounceManifestResponse { Ack = true });
        }

        public override Task<RequestManifestResponse> RequestManifest(RequestManifestRequest request, ServerCallContext context)
        {
            Console.WriteLine($"[gRPC] Received manifest request from {context.Peer}.");
            // Placeholder: In the future, this would look up and return the actual manifest.
            return Task.FromResult(new RequestManifestResponse());
        }

        public override Task DownloadChunk(DownloadChunkRequest request, IServerStreamWriter<DownloadChunkResponse> responseStream, ServerCallContext context)
        {
            Console.WriteLine($"[gRPC] Received chunk download request from {context.Peer}.");
            // Placeholder: In the future, this would stream the file chunks.
            return Task.CompletedTask;
        }
    }
}
