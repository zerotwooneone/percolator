using System;
using System.Threading.Tasks;
using Grpc.Core;
using Percolator.Contracts.Protos;

namespace Percolator.Application
{
    public class FileSharingService : FileSharing.FileSharingBase
    {
        private readonly ManifestStore _manifestStore;

        public FileSharingService(ManifestStore manifestStore)
        {
            _manifestStore = manifestStore;
        }

        public override Task<AnnounceManifestResponse> AnnounceManifest(AnnounceManifestRequest request, ServerCallContext context)
        {
            Console.WriteLine($"[gRPC] Received manifest announcement from {context.Peer}.");
            return Task.FromResult(new AnnounceManifestResponse { Ack = true });
        }

        public override Task<RequestManifestResponse> RequestManifest(RequestManifestRequest request, ServerCallContext context)
        {
            Console.WriteLine($"[gRPC] Received manifest request from {context.Peer} for hash: {request.ManifestHash.ToByteArray().Length} bytes");
            var manifest = _manifestStore.GetManifest(request.ManifestHash);
            var response = new RequestManifestResponse();
            if (manifest != null)
            {
                response.SignedManifest = manifest;
            }
            return Task.FromResult(response);
        }

        public override Task DownloadChunk(DownloadChunkRequest request, IServerStreamWriter<DownloadChunkResponse> responseStream, ServerCallContext context)
        {
            Console.WriteLine($"[gRPC] Received chunk download request from {context.Peer}.");
            // Placeholder: In the future, this would stream the file chunks.
            return Task.CompletedTask;
        }
    }
}
