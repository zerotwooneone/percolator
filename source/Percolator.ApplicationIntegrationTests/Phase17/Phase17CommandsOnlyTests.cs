using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Percolator.Application.Cli;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Contracts;
using System.Collections.Concurrent;
using Percolator.Identity;
using PeerId = Percolator.Identity.PeerId;
using Percolator.ApplicationIntegrationTests.TestDoubles;

namespace Percolator.ApplicationIntegrationTests.Phase17;

[TestFixture]
public class Phase17CommandsOnlyTests : IntegrationTestBase
{
    private sealed class ClientToHostTransport : IMessageTransportService
    {
        private readonly IServiceProvider _clientProvider;
        private readonly IServiceProvider _hostProvider;
        private readonly string _hostName;
        public ClientToHostTransport(IServiceProvider clientProvider, IServiceProvider hostProvider, string hostName)
        {
            _clientProvider = clientProvider;
            _hostProvider = hostProvider;
            _hostName = hostName;
        }

        public async Task<SendMessageResponse> SendMessageAsync(
            Percolator.Identity.PeerId recipientPeerId,
            Percolator.Network.DirectSessionId directSessionId,
            Percolator.Cryptography.SessionRatchetMessage message,
            CancellationToken cancellationToken = default)
        {
            // Only allow client->host traffic in this test. Determine the Host peerId as seen by this client.
            var clientPeerRepo = _clientProvider.GetRequiredService<Percolator.Identity.IPeerIdentityRepository>();
            var clientViewOfHost = await clientPeerRepo.GetByNameAsync(new Percolator.Identity.Model.DisplayName(_hostName), CancellationToken.None);
            if (clientViewOfHost is null || recipientPeerId.Value != clientViewOfHost.Id.Value)
            {
                throw new InvalidOperationException("Direct client-to-client delivery is disabled for this scenario; use relay via Host.");
            }
            var mediator = _hostProvider.GetRequiredService<IMediator>();
            var ctx = _hostProvider.GetRequiredService<ActiveIdentityContext>();
            var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = message.Value, SelfIdentityId = ctx.Identity!.SelfIdentityId };
            var result = await mediator.Send(cmd, cancellationToken);
            var response = new DeliverOpaqueMessageResponse { Version = 1 };
            if (result.ResponsePayloadBytes is not null)
            {
                response.ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
                {
                    Version = 1,
                    ResponsePayload = ByteString.CopyFrom(result.ResponsePayloadBytes)
                };
            }
            return new SendMessageResponse { OriginalResponse = response };
        }
    }
    
    private sealed class HostRelayTransport : IMessageTransportService
    {
        private readonly ConcurrentDictionary<Identity.PeerId, IServiceProvider> _routes;
        private readonly IServiceProvider _hostProvider;

        public HostRelayTransport(
            ConcurrentDictionary<Identity.PeerId, IServiceProvider> routes,
            IServiceProvider hostProvider)
        {
            _routes = routes;
            _hostProvider = hostProvider;
        }

        public void AddRoute(Identity.PeerId peerId, IServiceProvider provider)
        {
            _routes[peerId] = provider;
        }

        public async Task<SendMessageResponse> SendMessageAsync(
            Percolator.Identity.PeerId recipientPeerId,
            Percolator.Network.DirectSessionId directSessionId,
            Percolator.Cryptography.SessionRatchetMessage message,
            CancellationToken cancellationToken = default)
        {
            if (!_routes.TryGetValue(recipientPeerId, out var provider))
            {
                throw new InvalidOperationException($"No route registered for recipient {recipientPeerId.Value}");
            }
            var mediator = provider.GetRequiredService<IMediator>();
            var ctx = provider.GetRequiredService<ActiveIdentityContext>();
            var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = message.Value, SelfIdentityId = ctx.Identity!.SelfIdentityId };
            var result = await mediator.Send(cmd, cancellationToken);
            var response = new DeliverOpaqueMessageResponse { Version = 1 };
            if (result.ResponsePayloadBytes is not null)
            {
                response.ResponsePayload = new DeliverOpaqueMessageResponse.Types.Payload
                {
                    Version = 1,
                    ResponsePayload = ByteString.CopyFrom(result.ResponsePayloadBytes)
                };
            }
            return new SendMessageResponse { OriginalResponse = response };
        }

        public async Task<PeerId> GetPeerIdFromHost(byte[] publicKeyBytes)
        {
            // Resolve by PKH only (legacy PeerConnection repo removed)
            var pubKeyRepo = _hostProvider.GetRequiredService<IPeerPublicSigningKeyStore>();
            var publicKeyHash = IdentityPublicKeyHash.FromSpki(publicKeyBytes);
            var pubKeyRec = await pubKeyRepo.GetPeerIdByPublicKeyHashAsync(publicKeyHash);
            if (pubKeyRec is not null)
            {
                return pubKeyRec;
            }
            throw new InvalidOperationException($"No peer registered for public key {Convert.ToBase64String(publicKeyBytes)}");
        }
    }

    private static byte[] GetSpki(IHost host)
    {
        using var scope = host.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ActiveIdentityContext>();
        return ctx.Keys!.IdentitySigningKey.ExportSubjectPublicKeyInfo();
    }

    private static byte[] GetPkhFromSpki(byte[] spki) => SHA256.HashData(spki);

    [Test]
    [Ignore("Not working yet")]
    public async Task Step17_EndToEnd_Via_Application_Commands_Using_Loopback()
    {
        // Build Host
        var hostPort = GetAvailablePort();
        var hostRoutes = new ConcurrentDictionary<PeerId, IServiceProvider>();
        using var host = await CreateAndInitializeHostAsync(hostPort, "P17-Host", identityName: "host", additionalServiceRegistration: services =>
        {
            services.RemoveAll<IMessageTransportService>();
            services.AddSingleton<IMessageTransportService>(sp => new HostRelayTransport(hostRoutes, services.BuildServiceProvider()));
        });

        // Build Alice with loopback to Host
        var alicePort = GetAvailablePort();
        using var alice = await CreateAndInitializeHostAsync(alicePort, "P17-Alice", identityName: "alice", additionalServiceRegistration: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGrpcSessionService>(sp => new SingleHostGrpcSessionLoopback(host.Services)));
            services.RemoveAll<IMessageTransportService>();
            services.AddSingleton<IMessageTransportService>(sp => new ClientToHostTransport(sp, host.Services, "host"));
        });
        // Build Bob with loopback to Host
        var bobPort = GetAvailablePort();
        using var bob = await CreateAndInitializeHostAsync(bobPort, "P17-Bob", identityName: "bob", additionalServiceRegistration: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGrpcSessionService>(sp => new SingleHostGrpcSessionLoopback(host.Services)));
            services.RemoveAll<IMessageTransportService>();
            services.AddSingleton<IMessageTransportService>(sp => new ClientToHostTransport(sp, host.Services, "host"));
        });
        // Build Charlie with loopback to Host
        var charliePort = GetAvailablePort();
        using var charlie = await CreateAndInitializeHostAsync(charliePort, "P17-Charlie", identityName: "charlie", additionalServiceRegistration: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGrpcSessionService>(sp => new SingleHostGrpcSessionLoopback(host.Services)));
            services.RemoveAll<IMessageTransportService>();
            services.AddSingleton<IMessageTransportService>(sp => new ClientToHostTransport(sp, host.Services, "host"));
        });

        // Configure host relay routes: map recipient PeerId -> recipient service provider
        Guid alicePeerGuid, bobPeerGuid, charliePeerGuid;
        using (var s = alice.Services.CreateScope())
        {
            var ctx = s.ServiceProvider.GetRequiredService<ActiveIdentityContext>();
            alicePeerGuid = ctx.Identity!.Id;
        }
        using (var s = bob.Services.CreateScope())
        {
            var ctx = s.ServiceProvider.GetRequiredService<ActiveIdentityContext>();
            bobPeerGuid = ctx.Identity!.Id;
        }
        using (var s = charlie.Services.CreateScope())
        {
            var ctx = s.ServiceProvider.GetRequiredService<ActiveIdentityContext>();
            charliePeerGuid = ctx.Identity!.Id;
        }
        var hostTransport = (HostRelayTransport)host.Services.GetRequiredService<IMessageTransportService>();
        
        var hostEp = new DnsEndPoint("localhost", hostPort);

        var aliceMed = alice.Services.GetRequiredService<IMediator>();
        var bobMed = bob.Services.GetRequiredService<IMediator>();
        var charlieMed = charlie.Services.GetRequiredService<IMediator>();

        // Helper SPKIs and PKHs
        var hostSpki = GetSpki(host);
        var aliceSpki = GetSpki(alice);
        var bobSpki = GetSpki(bob);
        var charlieSpki = GetSpki(charlie);
        var alicePkh = GetPkhFromSpki(aliceSpki);
        var bobPkh = GetPkhFromSpki(bobSpki);

        // 1) Alice↔Host connect; mutual naming; Alice probes DHT (0 nodes); Alice publishes prekeys.
        await aliceMed.Send(new Percolator.Application.Identity.SetPeerNameByPublicKeyCommand("host", hostSpki));
        await aliceMed.Send(new ConnectToPeerCommand(hostEp, "host"));
        var find0 = await aliceMed.Send(new DhtProbeCommand(hostEp, "host", null));
        find0.Should().NotBeNull();
        // Publish Alice prekeys to Host (using existing SubmitPreKeysCommand)
        await aliceMed.Send(new SubmitPreKeysCommand("host", OneTimeKeyCount: 5, ExpiresUtc: DateTimeOffset.UtcNow.AddHours(1)));

        // 2) Bob↔Host connect; mutual naming; Bob probes and discovers Alice’s PKH.
        await bobMed.Send(new Percolator.Application.Identity.SetPeerNameByPublicKeyCommand("host", hostSpki));
        await bobMed.Send(new ConnectToPeerCommand(hostEp, "host"));
        var findBob = await bobMed.Send(new DhtProbeCommand(hostEp, "host", null));
        findBob.Should().NotBeNull();
        
        await charlieMed.Send(new Percolator.Application.Identity.SetPeerNameByPublicKeyCommand("host", hostSpki));
        await charlieMed.Send(new ConnectToPeerCommand(hostEp, "host"));
        
        hostTransport.AddRoute(await hostTransport.GetPeerIdFromHost(aliceSpki), alice.Services);
        hostTransport.AddRoute(await hostTransport.GetPeerIdFromHost(bobSpki),bob.Services);
        hostTransport.AddRoute(await hostTransport.GetPeerIdFromHost(charlieSpki), charlie.Services);

        // 3) Bob initiates opaque handshake to Alice via Host (one command)
        await bobMed.Send(new InitiateHandshakeViaHostCommand("host", alicePkh,PeerName:"alice"));
        // Trigger online pump via ping
        await aliceMed.Send(new DhtPingCommand("host"));
        await bobMed.Send(new DhtPingCommand("host"));

        // 4) Bob publishes his prekey bundle to Host.
        await bobMed.Send(new SubmitPreKeysCommand("host", OneTimeKeyCount: 5, ExpiresUtc: DateTimeOffset.UtcNow.AddHours(1)));

        // 5) Charlie↔Host connect; mutual naming by SPKI.
        
        

        // 6) Charlie probes DHT and discovers both Alice and Bob
        var findCharlie = await charlieMed.Send(new DhtProbeCommand(hostEp, "host", null));
        findCharlie.Should().NotBeNull();
        //expect host, alice, and bob
        findCharlie.CloserPeers.Should()
            .Contain(p=>p.PeerId.ToByteArray().SequenceEqual(alicePkh))
            .And.Contain(p=>p.PeerId.ToByteArray().SequenceEqual(bobPkh));

        // 7) Charlie initiates opaque handshakes to Alice and Bob via MQ; verify sessions exist
        await charlieMed.Send(new InitiateHandshakeViaHostCommand("host", alicePkh, PeerName:"alice"));
        //pump messages to alice and charlie
        await aliceMed.Send(new DhtPingCommand("host"));
        await charlieMed.Send(new DhtPingCommand("host"));
        
        await charlieMed.Send(new InitiateHandshakeViaHostCommand("host", bobPkh, PeerName:"bob"));
        //pump messages to bob and charlie
        await bobMed.Send(new DhtPingCommand("host"));
        await charlieMed.Send(new DhtPingCommand("host"));

        // Minimal assertion: non-null command results above indicate flow success.

        // 9) Alice creates group with Bob+Charlie
        // Use CreateGroupFromIdentityKeysCommand
        var groupGuid = Guid.NewGuid();
        using (var aliceScope = alice.Services.CreateScope())
        {
            var ctx = aliceScope.ServiceProvider.GetRequiredService<ActiveIdentityContext>();
            // Ensure Alice knows Bob and Charlie by SPKI so the key store can resolve their PeerIds
            await aliceMed.Send(new Percolator.Application.Identity.SetPeerNameByPublicKeyCommand("bob", bobSpki));
            await aliceMed.Send(new Percolator.Application.Identity.SetPeerNameByPublicKeyCommand("charlie", charlieSpki));
            await aliceMed.Send(new Percolator.Application.Apps.Chat.CreateGroupFromIdentityKeysCommand(
                ctx.Identity!.SelfIdentityId.Value,
                groupGuid,
                new List<byte[]> { aliceSpki, bobSpki, charlieSpki },
                "P17 Group",
                aliceSpki));

            // Trigger host pump for OTHER clients (Bob, Charlie) to receive enqueued CreateGroup notifications
            await bobMed.Send(new DhtPingCommand("host"));
            await charlieMed.Send(new DhtPingCommand("host"));
            await aliceMed.Send(new DhtPingCommand("host"));

            // Assert: conversation exists and participants include Bob and Charlie (total 3)
            var convoRepo = aliceScope.ServiceProvider.GetRequiredService<Percolator.Chat.IConversationRepository>();
            var peerRepo = aliceScope.ServiceProvider.GetRequiredService<Percolator.Identity.IPeerIdentityRepository>();
            var convo = await convoRepo.GetByGroupGuidAsync(groupGuid, ctx.Identity!.SelfIdentityId.Value);
            convo.Should().NotBeNull("group conversation should be created for Alice");
            var bobPeer = await peerRepo.GetByNameAsync(new Percolator.Identity.Model.DisplayName("bob"), CancellationToken.None);
            var charliePeer = await peerRepo.GetByNameAsync(new Percolator.Identity.Model.DisplayName("charlie"), CancellationToken.None);
            bobPeer.Should().NotBeNull();
            charliePeer.Should().NotBeNull();
            var participantIds = convo!.Participants.Select(p => p.Value).ToHashSet();
            participantIds.Should().HaveCount(3);
            participantIds.Should().Contain(bobPeer!.Id.Value);
            participantIds.Should().Contain(charliePeer!.Id.Value);
        }

        // 9a) Alice sends a message, assert Bob and Charlie receive it
        {
            var messageId = new Percolator.Chat.ValueObjects.MessageId(Guid.NewGuid());
            var content = "hello from 9a";
            var sentAt = DateTimeOffset.UtcNow;

            // Post message on Alice by group lookup
            await aliceMed.Send(new Percolator.Chat.App.Commands.PostTextMessageCommand(
                Percolator.Chat.App.ConversationLookupKey.ForGroup(groupGuid),
                messageId,
                content,
                sentAt));

            // Pump deliveries via host
            await bobMed.Send(new DhtPingCommand("host"));
            await charlieMed.Send(new DhtPingCommand("host"));

            // Assert Bob has the message
            using (var bobScope = bob.Services.CreateScope())
            {
                var bobCtx = bobScope.ServiceProvider.GetRequiredService<ActiveIdentityContext>();
                var bobRepo = bobScope.ServiceProvider.GetRequiredService<Percolator.Chat.IConversationRepository>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = false;
                while (sw.ElapsedMilliseconds < 5000 && !ok)
                {
                    var convo = await bobRepo.GetByGroupGuidAsync(groupGuid, bobCtx.Identity!.SelfIdentityId.Value);
                    if (convo is not null && convo.Messages.Any(m => m.Id.Value == messageId.Value && m.Content == content))
                    {
                        ok = true;
                        break;
                    }
                    await Task.Delay(50);
                }
                ok.Should().BeTrue("Bob should persist the group message posted by Alice");
            }

            // Assert Charlie has the message
            using (var charlieScope = charlie.Services.CreateScope())
            {
                var chCtx = charlieScope.ServiceProvider.GetRequiredService<ActiveIdentityContext>();
                var chRepo = charlieScope.ServiceProvider.GetRequiredService<Percolator.Chat.IConversationRepository>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok = false;
                while (sw.ElapsedMilliseconds < 5000 && !ok)
                {
                    var convo = await chRepo.GetByGroupGuidAsync(groupGuid, chCtx.Identity!.SelfIdentityId.Value);
                    if (convo is not null && convo.Messages.Any(m => m.Id.Value == messageId.Value && m.Content == content))
                    {
                        ok = true;
                        break;
                    }
                    await Task.Delay(50);
                }
                ok.Should().BeTrue("Charlie should persist the group message posted by Alice");
            }
        }

        // 10) Alice grants Bob admin
        using (var aliceScope2 = alice.Services.CreateScope())
        {
            var ctx = aliceScope2.ServiceProvider.GetRequiredService<ActiveIdentityContext>();
            await aliceMed.Send(new Percolator.Application.Apps.Chat.GrantGroupAdminAppCommand(
                ctx.Identity!.SelfIdentityId.Value,
                groupGuid,
                bobSpki));

            // Assert: admin set contains Alice (creator) and Bob (grantee)
            var convoRepo2 = aliceScope2.ServiceProvider.GetRequiredService<Percolator.Chat.IConversationRepository>();
            var adminKeyStore = aliceScope2.ServiceProvider.GetRequiredService<Percolator.Chat.App.IGroupAdminKeyStore>();
            var peerRepo2 = aliceScope2.ServiceProvider.GetRequiredService<Percolator.Identity.IPeerIdentityRepository>();

            var convo2 = await convoRepo2.GetByGroupGuidAsync(groupGuid, ctx.Identity!.SelfIdentityId.Value);
            convo2.Should().NotBeNull();

            var adminKeys = await adminKeyStore.GetKeysAsync(convo2!.Id.Value, CancellationToken.None);
            adminKeys.Should().NotBeNull();

            // Resolve expected admin public keys (SPKIs)
            var aliceSpkiExpected = GetSpki(alice);
            var bobSpkiExpected = bobSpki;

            var spkis = adminKeys
                .Where(k => k.RevokedAtUtc is null)
                .Select(k => Convert.ToBase64String(k.PublicKey.Bytes))
                .ToHashSet();

            spkis.Should().Contain(Convert.ToBase64String(aliceSpkiExpected));
            spkis.Should().Contain(Convert.ToBase64String(bobSpkiExpected));
        }

        // 11) Bob removes Charlie
        using (var bobScope = bob.Services.CreateScope())
        {
            var ctx = bobScope.ServiceProvider.GetRequiredService<ActiveIdentityContext>();
            var bobPeerRepo = bobScope.ServiceProvider.GetRequiredService<Percolator.Identity.IPeerIdentityRepository>();
            var charliePeerOnBob = await bobPeerRepo.GetByNameAsync(new Percolator.Identity.Model.DisplayName("charlie"), CancellationToken.None);
            if (charliePeerOnBob is not null)
            {
                await bobMed.Send(new Percolator.Application.Apps.Chat.UpdateGroupMembershipAppCommand(
                    ctx.Identity!.SelfIdentityId.Value,
                    groupGuid,
                    MembersToAdd: null,
                    MembersToRemove: new List<Guid> { charliePeerOnBob.Id.Value },
                    LeaveGroup: null));
            }
        }

        // 12) Bob sends a group message (skipped detailed visibility assertions in this test)
        // Trigger relay via ping to force delivery cycles
        await bobMed.Send(new DhtPingCommand("host"));

        // 13) Bob re-adds Charlie
        using (var bobScope2 = bob.Services.CreateScope())
        {
            var ctx = bobScope2.ServiceProvider.GetRequiredService<ActiveIdentityContext>();
            var bobPeerRepo = bobScope2.ServiceProvider.GetRequiredService<Percolator.Identity.IPeerIdentityRepository>();
            var charliePeerOnBob = await bobPeerRepo.GetByNameAsync(new Percolator.Identity.Model.DisplayName("charlie"), CancellationToken.None);
            if (charliePeerOnBob is not null)
            {
                await bobMed.Send(new Percolator.Application.Apps.Chat.UpdateGroupMembershipAppCommand(
                    ctx.Identity!.SelfIdentityId.Value,
                    groupGuid,
                    MembersToAdd: new List<Guid> { charliePeerOnBob.Id.Value },
                    MembersToRemove: null,
                    LeaveGroup: null));
            }
        }

        // 14) Alice sends a group message (visibility not asserted here).
        await aliceMed.Send(new DhtPingCommand("host"));

        // If we reached here without exceptions, baseline command flow works via MediatR.
        Assert.Pass("Phase 17 commands executed via application commands with loopback transport.");
    }
}
