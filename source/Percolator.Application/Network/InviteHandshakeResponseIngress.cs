using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Percolator.Application.Network.Handshake;
using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Network;

public interface IInviteHandshakeResponseIngress
{
    Task HandleAsync(SelfId selfIdentityId, InviteHandshakeResponse response, CancellationToken ct = default);
}

internal sealed class InviteHandshakeResponseIngress : IInviteHandshakeResponseIngress
{
    private readonly IMediator _mediator;

    public InviteHandshakeResponseIngress(IMediator mediator)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
    }

    public Task HandleAsync(SelfId selfIdentityId, InviteHandshakeResponse response, CancellationToken ct = default)
    {
        if (response is null) throw new ArgumentNullException(nameof(response));

        if (!response.HasInitialRatchetMessage || response.InitialRatchetMessage.Length == 0)
        {
            throw new InvalidOperationException("initial_ratchet_message is required.");
        }

        return _mediator.Send(
            new HandleHandshakeResponderHelloCommand(selfIdentityId, response),
            ct);
    }
}
