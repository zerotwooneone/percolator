using Grpc.Core;
using Grpc.Core.Interceptors;
using Percolator.Application.Chat;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Infrastructure.Network.Grpc;

public sealed class DeliveryCertificateAuthInterceptor : Interceptor
{
    private readonly IPeerAuthenticationService _peerAuthenticationService;

    public DeliveryCertificateAuthInterceptor(IPeerAuthenticationService peerAuthenticationService)
    {
        _peerAuthenticationService = peerAuthenticationService ?? throw new ArgumentNullException(nameof(peerAuthenticationService));
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        // Extract metadata headers
        var senderPublicIdentityId = context.RequestHeaders.GetValue("x-percolator-sender-public-identity-id");
        if (senderPublicIdentityId is null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing sender PublicIdentityId header"));
        }

        var timestampStr = context.RequestHeaders.GetValue("x-percolator-timestamp");
        if (timestampStr is null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing timestamp header"));
        }

        var signatureStr = context.RequestHeaders.GetValue("x-percolator-signature");
        if (signatureStr is null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing signature header"));
        }

        if (!long.TryParse(timestampStr, out var timestampUnix))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid timestamp format"));
        }

        var requestTimestamp = DateTimeOffset.FromUnixTimeSeconds(timestampUnix);
        var signatureBytes = Convert.FromBase64String(signatureStr);
        var signature = Signature.FromBytes(signatureBytes);

        // Convert string PublicIdentityId to PublicIdentityId type
        var publicIdentityIdBytes = Convert.FromHexString(senderPublicIdentityId);
        var senderPublicIdentityIdTyped = new PublicIdentityId(new Guid(publicIdentityIdBytes));

        // Call authentication service
        var isAuthenticated = await _peerAuthenticationService.AuthenticateDeliveryCertificateRequestAsync(
            senderPublicIdentityIdTyped,
            requestTimestamp,
            signature,
            context.CancellationToken);

        if (!isAuthenticated)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Authentication failed"));
        }

        return await continuation(request, context);
    }
}
