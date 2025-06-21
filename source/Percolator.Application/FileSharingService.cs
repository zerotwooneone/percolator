using System;
using System.Threading.Tasks;
using Grpc.Core;
using Percolator.Contracts.Protos;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using Google.Protobuf;

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

            // TODO: Add a trust model for signers
            if (!_manifestService.VerifyManifest(request.SignedManifest))
            {
                Console.WriteLine($"[gRPC] Discarding manifest with invalid signature from {context.Peer}.");
                return Task.FromResult(new AnnounceManifestResponse { Success = false, Message = "Invalid signature" });
            }

            // If we get here, the signature is valid.
            var manifestHash = ByteString.CopyFrom(SHA256.HashData(request.SignedManifest.Manifest.ToByteArray()));
            _manifestStore.StoreManifest(manifestHash, request.SignedManifest);
            Console.WriteLine($"[gRPC] Received and verified manifest with hash {manifestHash.ToBase64()} from {context.Peer}.");

            return Task.FromResult(new AnnounceManifestResponse
            {
                Success = true,
                Message = "Manifest received."
            });
        }

        public override Task<RequestManifestResponse> RequestManifest(RequestManifestRequest request, ServerCallContext context)
        {
            if (request.HasSubPath && !string.IsNullOrEmpty(request.SubPath))
            {
                // On-demand generation for a subdirectory
                Console.WriteLine($"[gRPC] Received request for subdirectory '{request.SubPath}' within manifest {request.ManifestHash.ToBase64()}.");
                var (rootManifest, rootPath) = _manifestStore.GetManifestAndRootPath(request.ManifestHash);

                if (rootManifest is null || rootPath is null)
                {
                    return Task.FromResult(new RequestManifestResponse()); // Not found or not a local manifest
                }

                var subPath = Path.GetFullPath(Path.Combine(rootPath, request.SubPath));

                // Security check: ensure the requested subpath is actually within the original root path
                if (!subPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[gRPC] SECURITY: Denied request for invalid sub-path '{request.SubPath}'.");
                    return Task.FromResult(new RequestManifestResponse());
                }

                var (_, subManifest) = _manifestService.CreateManifestFromFile(subPath);
                return Task.FromResult(new RequestManifestResponse { SignedManifest = subManifest });
            }
            else
            {
                // Standard request for a manifest by its hash
                var manifest = _manifestStore.GetManifest(request.ManifestHash);
                return Task.FromResult(new RequestManifestResponse { SignedManifest = manifest });
            }
        }

        public override Task DownloadChunk(DownloadChunkRequest request, IServerStreamWriter<DownloadChunkResponse> responseStream, ServerCallContext context)
        {
            Console.WriteLine($"[gRPC] Received chunk download request from {context.Peer}.");
            // Placeholder: In the future, this would stream the file chunks.
            return Task.CompletedTask;
        }
    }
}
