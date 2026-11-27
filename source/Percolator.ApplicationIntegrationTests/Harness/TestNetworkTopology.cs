namespace Percolator.ApplicationIntegrationTests.Harness;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Infrastructure.Transport;

public sealed class TestNetworkTopology : IAsyncDisposable
{
    private readonly List<TestRelayServer> _relays = new();
    private readonly List<TestPersonaNode> _personas = new();

    public IReadOnlyList<TestRelayServer> Relays => _relays;
    public IReadOnlyList<TestPersonaNode> Personas => _personas;

    public async Task<TestRelayServer> CreateRelayAsync(
        RelayHostingPolicy? initialPolicy = null,
        AuthenticatedMemberRateLimiterOptions? rateLimiterOptions = null)
    {
        var relay = await TestRelayServer.StartAsync(initialPolicy, rateLimiterOptions).ConfigureAwait(false);
        _relays.Add(relay);
        return relay;
    }

    public async Task<TestPersonaNode> CreatePersonaAsync(
        string displayName = "Persona",
        string? sqliteFilePath = null,
        bool blockDirectIce = false)
    {
        var persona = await TestPersonaNode.CreateAsync(displayName, sqliteFilePath, blockDirectIce).ConfigureAwait(false);
        _personas.Add(persona);
        return persona;
    }

    public void TrackPersona(TestPersonaNode persona) => _personas.Add(persona);

    public async Task PairPersonasAsync(TestPersonaNode inviter, TestPersonaNode invitee, TestRelayServer sharedRelay)
    {
        // 1. Inviter exports invitation
        var inviteUri = await inviter.CreateInvitationAsync(sharedRelay).ConfigureAwait(false);

        // 2. Invitee connects with invitation (1-RTT fetch PreKeyBundle + verify pinned IdentityKey + send 4-DH PreKeyMessage)
        var inviteeChannelId = await invitee.ConnectWithInvitationAsync(inviteUri).ConfigureAwait(false);

        // 3. Inviter drains and receives inbound contact request
        await inviter.WaitForInboundContactRequestAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // 4. Inviter approves contact
        await inviter.ApproveContactAsync(invitee.IdentityId).ConfigureAwait(false);

        // 5. Inviter sends profile capability / initial reply to deliver Bob's DeliveryToken to Alice
        var inviterChannelId = ChannelId.DeterministicForDirect(inviter.IdentityId, invitee.IdentityId, sharedRelay.Endpoint);
        await inviter.SendProfileCapabilityAsync(inviterChannelId, invitee.IdentityId).ConfigureAwait(false);

        // 6. Confirm channel activation on both sides
        await invitee.WaitForChannelStatusAsync(inviteeChannelId, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await inviter.WaitForChannelStatusAsync(inviterChannelId, DirectChannelStatus.Active, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var persona in _personas)
        {
            await persona.DisposeAsync().ConfigureAwait(false);
        }
        foreach (var relay in _relays)
        {
            await relay.DisposeAsync().ConfigureAwait(false);
        }
    }
}
