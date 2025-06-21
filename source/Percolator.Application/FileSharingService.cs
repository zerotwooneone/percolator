using System;
using System.Threading.Tasks;
using Grpc.Core;
using Percolator.Contracts.Protos;

namespace Percolator.Application
{
    public class FileSharingService : FileSharing.FileSharingBase
    {
        private readonly ManifestStore _manifestStore;
        private readonly ManifestService _manifestService;

        public FileSharingService(ManifestStore manifestStore, ManifestService manifestService)
        {
            _manifestStore = manifestStore;
            _manifestService = manifestService;
        }

        public override Task<AnnounceManifestResponse> AnnounceManifest(AnnounceManifestRequest request, ServerCallContext context)
        {
            Console.WriteLine($"[gRPC] Received manifest announcement from {context.Peer}.");

            // 1. Verify the signature
            if (request.SignedManifest is null || !_manifestService.VerifyManifest(request.SignedManifest))
            {
                Console.WriteLine($"[gRPC] WARNING: Received manifest with invalid signature from {context.Peer}. Discarding.");
                var invalidResponse = new AnnounceManifestResponse
                {
                    Success = false,
                    Message = "Invalid signature."
                };
                return Task.FromResult(invalidResponse);
            }

            // 2. Store the manifest if valid
            var manifestHash = request.ManifestHash;
            var signedManifest = request.SignedManifest;

            _manifestStore.StoreManifest(manifestHash, signedManifest);
            Console.WriteLine($"[gRPC] Stored manifest {manifestHash.ToBase64()} from {context.Peer}.");

            var response = new AnnounceManifestResponse
            {
                Success = true,
                Message = "Manifest received."
            };
            return Task.FromResult(response);
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
