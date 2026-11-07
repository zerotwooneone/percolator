using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Simulator.Features.DirectChat;

public sealed record SimulatorChatMessageReadModel(
    Guid MessageId,
    Guid ChannelId,
    PublicIdentityId AuthorId,
    string Content,
    ulong TimestampUnixMs,
    ulong? DeliveredAtUnixMs,
    ulong? ReadAtUnixMs,
    ulong? EditedAtUnixMs,
    IReadOnlyList<string> Reactions);

public interface ISimulatorChatQueryService
{
    Task<IReadOnlyList<SimulatorChatMessageReadModel>> GetTranscriptAsync(
        ChannelId channelId,
        CancellationToken ct = default);

    Task<bool> HasMessageContainingAsync(
        string expectedText,
        CancellationToken ct = default);
}
