using Grpc.Core;
using Grpc.Core.Interceptors;
using Percolator.Application.Chat;
using Percolator.Chat.Messaging.ValueObjects;
using Percolator.Cryptography;

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
        var senderPkh = context.RequestHeaders.GetValue("x-percolator-sender-pkh");
        if (senderPkh is null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Missing sender PKH header"));
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

        // Convert string PKH to Pkh type
        var pkhBytes = Convert.FromHexString(senderPkh);
        var senderPkhTyped = Pkh.FromBytes(pkhBytes);

        // Call authentication service
        var isAuthenticated = await _peerAuthenticationService.AuthenticateDeliveryCertificateRequestAsync(
            senderPkhTyped,
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
