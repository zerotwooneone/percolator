namespace Percolator.ApplicationIntegrationTests.Harness;

using System;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.PluginSdk.Events;

public sealed record ChatMessageReceivedEvent(
    ChannelId ChannelId,
    PublicIdentityId AuthorId,
    string Plaintext,
    DateTimeOffset OccurredOnUtc,
    Guid EventId = default) : IApplicationEvent
{
    public Guid EventId { get; init; } = EventId == default ? Guid.NewGuid() : EventId;
}
