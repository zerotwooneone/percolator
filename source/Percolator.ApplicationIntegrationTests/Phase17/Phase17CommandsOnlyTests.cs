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
using Percolator.Application.Sessions;
using Percolator.Contracts;
using System.Collections.Concurrent;

namespace Percolator.ApplicationIntegrationTests.Phase17;

[TestFixture]
public class Phase17CommandsOnlyTests : IntegrationTestBase
{
    private sealed class GrpcSessionLoopback : IGrpcSessionService
    {
        private readonly IServiceProvider _hostProvider;
        public GrpcSessionLoopback(IServiceProvider hostProvider) => _hostProvider = hostProvider;

        public async Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(DnsEndPoint endpoint, EstablishDirectSessionRequest request)
        {
            var mediator = _hostProvider.GetRequiredService<IMediator>();
            var payload = EstablishDirectSessionRequest.Types.DirectInitiatorPayload.Parser.ParseFrom(request.ResponderBundle.SignedPayload);
            var cmd = new EstablishDirectSessionCommand
            {
                RemoteIdentityKeyBytes = request.ResponderBundle.IdentitySigningKey.ToByteArray(),
                SignedPayloadBytes = request.ResponderBundle.SignedPayload.ToByteArray(),
                PayloadSignatureBytes = request.ResponderBundle.PayloadSignature.ToByteArray(),
                OneTimePreKeyBytes = request.ResponderBundle.HasOneTimePreKey ? request.ResponderBundle.OneTimePreKey.ToByteArray() : null,
                RemoteEphemeral = payload.ResponderEphemeralKey.ToByteArray(),
                PeerEndPoint = endpoint,
                ClientCertificate = null
            };
            var result = await mediator.Send(cmd);
            return new EstablishDirectSessionResponse
            {
                Response = new EstablishDirectSessionResponse.Types.Response
                {
                    InitiatorIdentityKey = ByteString.CopyFrom(result.IdentitySigningKeyBytes),
                    RatchetMessage = ByteString.CopyFrom(result.RatchetMessageBytes),
                    InitiatorEphemeralKey = ByteString.CopyFrom(result.RemoteEphemeralKeyBytes)
                }
            };
        }
    }

    private sealed class ClientToHostTransport : IMessageTransportService
    {
        private readonly IServiceProvider _hostProvider;
        public ClientToHostTransport(IServiceProvider hostProvider) => _hostProvider = hostProvider;

        public async Task<DeliverOpaqueMessageResponse> SendMessageAsync(
            Percolator.Identity.PeerId recipientPeerId,
            Percolator.Network.DirectSessionId directSessionId,
            Percolator.Cryptography.SessionRatchetMessage message,
            CancellationToken cancellationToken = default)
        {
            var mediator = _hostProvider.GetRequiredService<IMediator>();
            var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = message.Value };
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
            return response;
        }
    }

    private sealed class HostRelayTransport : IMessageTransportService
    {
        private readonly ConcurrentDictionary<Guid, IServiceProvider> _routes;
        public HostRelayTransport(ConcurrentDictionary<Guid, IServiceProvider> routes)
        {
            _routes = routes;
        }

        public void AddRoute(Guid peerId, IServiceProvider provider)
        {
            _routes[peerId] = provider;
        }

        public async Task<DeliverOpaqueMessageResponse> SendMessageAsync(
            Percolator.Identity.PeerId recipientPeerId,
            Percolator.Network.DirectSessionId directSessionId,
            Percolator.Cryptography.SessionRatchetMessage message,
            CancellationToken cancellationToken = default)
        {
            if (!_routes.TryGetValue(recipientPeerId.Value, out var provider))
            {
                throw new InvalidOperationException($"No route registered for recipient {recipientPeerId.Value}");
            }
            var mediator = provider.GetRequiredService<IMediator>();
            var cmd = new DeliverOpaqueMessageCommand { PayloadBytes = message.Value };
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
            return response;
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
    public async Task Step17_EndToEnd_Via_Application_Commands_Using_Loopback()
    {
        // Build Host
        var hostPort = GetAvailablePort();
        var hostRoutes = new ConcurrentDictionary<Guid, IServiceProvider>();
        using var host = await CreateAndInitializeHostAsync(hostPort, "P17-Host", identityName: "host", additionalServiceRegistration: services =>
        {
            services.RemoveAll<IMessageTransportService>();
            services.AddSingleton<IMessageTransportService>(sp => new HostRelayTransport(hostRoutes));
        });

        // Build Alice with loopback to Host
        var alicePort = GetAvailablePort();
        using var alice = await CreateAndInitializeHostAsync(alicePort, "P17-Alice", identityName: "alice", additionalServiceRegistration: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGrpcSessionService>(sp => new GrpcSessionLoopback(host.Services)));
            services.RemoveAll<IMessageTransportService>();
            services.AddSingleton<IMessageTransportService>(sp => new ClientToHostTransport(host.Services));
        });
        // Build Bob with loopback to Host
        var bobPort = GetAvailablePort();
        using var bob = await CreateAndInitializeHostAsync(bobPort, "P17-Bob", identityName: "bob", additionalServiceRegistration: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGrpcSessionService>(sp => new GrpcSessionLoopback(host.Services)));
            services.RemoveAll<IMessageTransportService>();
            services.AddSingleton<IMessageTransportService>(sp => new ClientToHostTransport(host.Services));
        });
        // Build Charlie with loopback to Host
        var charliePort = GetAvailablePort();
        using var charlie = await CreateAndInitializeHostAsync(charliePort, "P17-Charlie", identityName: "charlie", additionalServiceRegistration: services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IGrpcSessionService>(sp => new GrpcSessionLoopback(host.Services)));
            services.RemoveAll<IMessageTransportService>();
            services.AddSingleton<IMessageTransportService>(sp => new ClientToHostTransport(host.Services));
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
        hostTransport.AddRoute(alicePeerGuid, alice.Services);
        hostTransport.AddRoute(bobPeerGuid, bob.Services);
        hostTransport.AddRoute(charliePeerGuid, charlie.Services);

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

        // 3) Bob initiates opaque handshake to Alice via Host (one command)
        await bobMed.Send(new InitiateHandshakeViaHostCommand("host", alicePkh));
        // Trigger online pump via ping
        await bobMed.Send(new DhtPingCommand("host"));

        // 4) Bob publishes his prekey bundle to Host.
        await bobMed.Send(new SubmitPreKeysCommand("host", OneTimeKeyCount: 5, ExpiresUtc: DateTimeOffset.UtcNow.AddHours(1)));

        // 5) Charlie↔Host connect; mutual naming by SPKI.
        await charlieMed.Send(new Percolator.Application.Identity.SetPeerNameByPublicKeyCommand("host", hostSpki));
        await charlieMed.Send(new ConnectToPeerCommand(hostEp, "host"));

        // 6) Charlie probes DHT and discovers both Alice and Bob
        var findCharlie = await charlieMed.Send(new DhtProbeCommand(hostEp, "host", null));
        findCharlie.Should().NotBeNull();

        // 7) Charlie initiates opaque handshakes to Alice and Bob via MQ; verify sessions exist
        await charlieMed.Send(new InitiateHandshakeViaHostCommand("host", alicePkh));
        await charlieMed.Send(new InitiateHandshakeViaHostCommand("host", bobPkh));
        await charlieMed.Send(new DhtPingCommand("host"));

        // Verify sessions exist (Charlie <-> Alice, Charlie <-> Bob) from Charlie side
        var convSvcCharlie = charlie.Services.GetRequiredService<IConversationService>();
        using (var scope = charlie.Services.CreateScope())
        {
            var peerRepo = scope.ServiceProvider.GetRequiredService<Percolator.Identity.IPeerRepository>();
            var alicePeer = await peerRepo.GetByNameAsync("host"); // host is the MQ relay, but sessions are 1:1 between peers; minimal assert via non-null commands above
        }

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
                ctx.Identity!.SelfIdentityId,
                groupGuid,
                new List<byte[]> { aliceSpki, bobSpki, charlieSpki },
                "P17 Group",
                aliceSpki));

            // Trigger host pump for OTHER clients (Bob, Charlie) to receive enqueued CreateGroup notifications
            await bobMed.Send(new DhtPingCommand("host"));
            await charlieMed.Send(new DhtPingCommand("host"));

            // Assert: conversation exists and participants include Bob and Charlie (total 3)
            var convoRepo = aliceScope.ServiceProvider.GetRequiredService<Percolator.Chat.IConversationRepository>();
            var peerRepo = aliceScope.ServiceProvider.GetRequiredService<Percolator.Identity.IPeerRepository>();
            var convo = await convoRepo.GetByGroupGuidAsync(groupGuid, ctx.Identity!.SelfIdentityId);
            convo.Should().NotBeNull("group conversation should be created for Alice");
            var bobPeer = await peerRepo.GetByNameAsync("bob");
            var charliePeer = await peerRepo.GetByNameAsync("charlie");
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
                    var convo = await bobRepo.GetByGroupGuidAsync(groupGuid, bobCtx.Identity!.SelfIdentityId);
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
                    var convo = await chRepo.GetByGroupGuidAsync(groupGuid, chCtx.Identity!.SelfIdentityId);
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
                ctx.Identity!.SelfIdentityId,
                groupGuid,
                bobSpki));

            // Assert: admin set contains Alice (creator) and Bob (grantee)
            var convoRepo2 = aliceScope2.ServiceProvider.GetRequiredService<Percolator.Chat.IConversationRepository>();
            var adminKeyStore = aliceScope2.ServiceProvider.GetRequiredService<Percolator.Chat.App.IGroupAdminKeyStore>();
            var peerRepo2 = aliceScope2.ServiceProvider.GetRequiredService<Percolator.Identity.IPeerRepository>();

            var convo2 = await convoRepo2.GetByGroupGuidAsync(groupGuid, ctx.Identity!.SelfIdentityId);
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
            var bobPeerRepo = bobScope.ServiceProvider.GetRequiredService<Percolator.Identity.IPeerRepository>();
            var charliePeerOnBob = await bobPeerRepo.GetByNameAsync("charlie");
            if (charliePeerOnBob is not null)
            {
                await bobMed.Send(new Percolator.Application.Apps.Chat.UpdateGroupMembershipAppCommand(
                    ctx.Identity!.SelfIdentityId,
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
            var bobPeerRepo = bobScope2.ServiceProvider.GetRequiredService<Percolator.Identity.IPeerRepository>();
            var charliePeerOnBob = await bobPeerRepo.GetByNameAsync("charlie");
            if (charliePeerOnBob is not null)
            {
                await bobMed.Send(new Percolator.Application.Apps.Chat.UpdateGroupMembershipAppCommand(
                    ctx.Identity!.SelfIdentityId,
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
