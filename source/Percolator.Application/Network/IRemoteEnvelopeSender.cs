using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Contracts;
using Percolator.Identity;

namespace Percolator.Application.Network
{
    public interface IRemoteEnvelopeSender
    {
        Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, RecipientRoute recipient, CancellationToken ct = default);
    }

    public sealed record RecipientRoute(PeerId PeerId, byte[]? PublicKeyHash);
}
