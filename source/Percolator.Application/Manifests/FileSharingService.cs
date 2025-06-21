using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.RateLimiting;
using Percolator.Contracts.Protos;
using Percolator.Cryptography;

namespace Percolator.Application.Manifests
{
    public class FileSharingService : FileSharing.FileSharingBase
    {
        private readonly IMediator _mediator;
        private readonly IManifestStore _manifestStore;
        private readonly ISignatureService _signatureService;
        private readonly IRateLimiter _rateLimiter;
        private readonly ILogger<FileSharingService> _logger;

        public FileSharingService(IMediator mediator, IManifestStore manifestStore, ISignatureService signatureService, IRateLimiter rateLimiter, ILogger<FileSharingService> logger)
        {
            _mediator = mediator;
            _manifestStore = manifestStore;
            _signatureService = signatureService;
            _rateLimiter = rateLimiter;
            _logger = logger;
        }

        public override Task<AnnounceManifestResponse> AnnounceManifest(AnnounceManifestRequest request, ServerCallContext context)
        {
            var decision = _rateLimiter.IsRequestAllowed(context.Peer);
            if (!decision.IsAllowed)
            {
                _logger.LogWarning("Rate limit exceeded for peer {Peer}. Throttling request.", context.Peer);
                return Task.FromResult(new AnnounceManifestResponse
                {
                    Success = false,
                    Message = "Rate limit exceeded.",
                    RetryAfterUtc = Timestamp.FromDateTime(decision.RetryAfterUtc!.Value)
                });
            }

            _logger.LogInformation("Received a manifest announcement from a peer.");

            if (request.SignedManifest is null)
            {
                return Task.FromResult(new AnnounceManifestResponse { Success = false, Message = "Request did not contain a manifest." });
            }

            // 1. Verify the signature
            var isSignatureValid = _signatureService.Verify(request.SignedManifest);
            if (!isSignatureValid)
            {
                _logger.LogWarning("Received a manifest with an invalid signature.");
                return Task.FromResult(new AnnounceManifestResponse { Success = false, Message = "Invalid signature." });
            }

            // 2. Calculate the hash of the inner manifest to use as the key
            using var sha256 = SHA256.Create();
            var manifestHash = ByteString.CopyFrom(sha256.ComputeHash(request.SignedManifest.Manifest.ToByteArray()));

            // 3. Store the manifest
            _manifestStore.Add(manifestHash, request.SignedManifest);
            _logger.LogInformation("Successfully stored manifest with hash {ManifestHash}", manifestHash.ToBase64());

            return Task.FromResult(new AnnounceManifestResponse { Success = true, Message = "Manifest accepted." });
        }

        public override Task<RequestManifestResponse> RequestManifest(RequestManifestRequest request, ServerCallContext context)
        {
            var decision = _rateLimiter.IsRequestAllowed(context.Peer);
            if (!decision.IsAllowed)
            {
                _logger.LogWarning("Rate limit exceeded for peer {Peer}. Throttling request.", context.Peer);
                return Task.FromResult(new RequestManifestResponse
                {
                    RetryAfterUtc = Timestamp.FromDateTime(decision.RetryAfterUtc!.Value)
                });
            }

            _logger.LogInformation("Received a request for manifest {ManifestHash}", request.ManifestHash.ToBase64());

            var manifest = _manifestStore.Get(request.ManifestHash);

            if (manifest is null)
            {
                _logger.LogWarning("Could not find manifest with hash {ManifestHash}", request.ManifestHash.ToBase64());
                return Task.FromResult(new RequestManifestResponse()); // Return empty response
            }

            // TODO: Handle sub-path requests to return partial manifests.
            // For now, we return the whole thing.

            _logger.LogInformation("Found manifest {ManifestHash}, returning it.", request.ManifestHash.ToBase64());
            return Task.FromResult(new RequestManifestResponse { SignedManifest = manifest });
        }

        public override Task DownloadChunk(DownloadChunkRequest request, IServerStreamWriter<DownloadChunkResponse> responseStream, ServerCallContext context)
        {
            var decision = _rateLimiter.IsRequestAllowed(context.Peer);
            if (!decision.IsAllowed)
            {
                _logger.LogWarning("Rate limit exceeded for peer {Peer}. Throttling request.", context.Peer);
                var metadata = new Metadata { { "retry-after-utc", decision.RetryAfterUtc!.Value.ToString("o") } };
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "Rate limit exceeded. Try again later."), metadata);
            }

            _logger.LogInformation("[gRPC] Received chunk download request from {Peer}.", context.Peer);

            // Placeholder: In the future, this would stream the file chunks.
            return Task.CompletedTask;
        }
    }
}
