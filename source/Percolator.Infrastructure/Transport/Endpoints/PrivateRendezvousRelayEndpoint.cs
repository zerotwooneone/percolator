namespace Percolator.Infrastructure.Transport.Endpoints;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Percolator.Apps.Discovery.Features.PrivateRendezvous;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Protos;

public sealed class PrivateRendezvousRelayEndpoint : PrivateRendezvousRelayService.PrivateRendezvousRelayServiceBase
{
    private readonly IPrivateRendezvousService _rendezvousService;

    public PrivateRendezvousRelayEndpoint(IPrivateRendezvousService rendezvousService)
    {
        _rendezvousService = rendezvousService ?? throw new ArgumentNullException(nameof(rendezvousService));
    }

    public override async Task<BlindedRendezvousResponseProto> PublishRendezvous(
        BlindedRendezvousPublishProto request,
        ServerCallContext context)
    {
        if (request.BlindedLocator.Length != 32)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Blinded locator must be 32 bytes."));
        }

        var locatorResult = BlindedLocator.FromSpan(request.BlindedLocator.Span);
        if (locatorResult.IsFailure)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, locatorResult.Error.Description));
        }

        var locator = locatorResult.Value;

        if (request.EncryptedRoute.Length > 0)
        {
            var routeResult = EncryptedRendezvousRoute.FromBytes(request.EncryptedRoute.Span);
            if (routeResult.IsFailure)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, routeResult.Error.Description));
            }

            var result = await _rendezvousService.PublishEncryptedRendezvousAsync(locator, routeResult.Value, context.CancellationToken);
            if (!result.IsSuccess)
            {
                throw new RpcException(new Status(StatusCode.Internal, result.Error.Description));
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.RouteDescriptor))
        {
            var result = await _rendezvousService.PublishRendezvousAsync(locator, request.RouteDescriptor, context.CancellationToken);
            if (!result.IsSuccess)
            {
                throw new RpcException(new Status(StatusCode.Internal, result.Error.Description));
            }
        }

        return new BlindedRendezvousResponseProto();
    }

    public override async Task<BlindedRendezvousResponseProto> QueryRendezvous(
        BlindedRendezvousQueryProto request,
        ServerCallContext context)
    {
        var locators = new List<BlindedLocator>();
        foreach (var b in request.BlindedLocators)
        {
            if (b.Length == 32)
            {
                var locResult = BlindedLocator.FromSpan(b.Span);
                if (locResult.IsSuccess)
                {
                    locators.Add(locResult.Value);
                }
            }
        }

        var matches = await _rendezvousService.ResolveRendezvousAsync(locators, context.CancellationToken);
        var response = new BlindedRendezvousResponseProto();

        foreach (var m in matches)
        {
            response.Matches.Add(new BlindedRendezvousMatchProto
            {
                BlindedLocator = ByteString.CopyFrom(m.Locator.Span),
                EncryptedRoute = ByteString.CopyFrom(m.EncryptedRoute.Span),
                RouteDescriptor = m.RelayRouteDescriptor?.Value ?? string.Empty
            });
        }

        return response;
    }
}
