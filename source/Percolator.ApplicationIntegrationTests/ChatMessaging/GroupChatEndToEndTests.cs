using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Percolator.Application.Network;
using Percolator.Contracts;
using MediatR;
using System.Security.Cryptography;
using Percolator.Application.Identity;
using Percolator.Application.Cli;
using Percolator.Prekey.DependencyInjection;

namespace Percolator.ApplicationIntegrationTests.ChatMessaging
{
    [TestFixture]
    [NonParallelizable]
    public class GroupChatEndToEndTests : IntegrationTestBase
    {
        private sealed class GrpcSessionLoopback : IGrpcSessionService
        {
            private readonly Func<DnsEndPoint, IServiceProvider?> _resolver;
            public GrpcSessionLoopback(Func<DnsEndPoint, IServiceProvider?> resolver) => _resolver = resolver;

            public async Task<EstablishDirectSessionResponse> EstablishDirectSessionAsync(DnsEndPoint endpoint, EstablishDirectSessionRequest request)
            {
                // Route to the destination node determined by the endpoint
                var provider = _resolver(endpoint) ?? throw new InvalidOperationException($"No destination for endpoint {endpoint}");
                var mediator = provider.GetRequiredService<IMediator>();
                var payload = EstablishDirectSessionRequest.Types.DirectInitiatorPayload.Parser.ParseFrom(request.InitiatorBundle.SignedPayload);
                var command = new EstablishDirectSessionCommand
                {
                    IdentitySigningKeyBytes = request.InitiatorBundle.IdentitySigningKey.ToByteArray(),
                    SignedPayloadBytes = request.InitiatorBundle.SignedPayload.ToByteArray(),
                    PayloadSignatureBytes = request.InitiatorBundle.PayloadSignature.ToByteArray(),
                    OneTimePreKeyBytes = request.InitiatorBundle.HasOneTimePreKey ? request.InitiatorBundle.OneTimePreKey.ToByteArray() : null,
                    PreKeyBytes = payload.SignedPreKey.ToByteArray(),
                    PeerEndPoint = endpoint,
                    ClientCertificate = null
                };
                var result = await mediator.Send(command);
                return new EstablishDirectSessionResponse
                {
                    Response = new EstablishDirectSessionResponse.Types.Response
                    {
                        IdentitySigningKey = ByteString.CopyFrom(result.IdentitySigningKeyBytes),
                        ResponsePayload = ByteString.CopyFrom(result.ResponsePayloadBytes),
                        PayloadSignature = ByteString.CopyFrom(result.PayloadSignatureBytes)
                    }
                };
            }
        }

        private IServiceProvider? ResolveProviderByEndpoint(DnsEndPoint endPoint)
        {
            if (endPoint.Port == _hostPort) return _host.Services;
            if (endPoint.Port == _alicePort) return _alice.Services;
            if (endPoint.Port == _bobPort) return _bob.Services;
            if (endPoint.Port == _charliePort) return _charlie.Services;
            return null;
        }

        private IServiceProvider? ResolveProviderByName(string name)
        {
            if (string.Equals(name, "host", StringComparison.OrdinalIgnoreCase)) return _host.Services;
            if (string.Equals(name, "alice", StringComparison.OrdinalIgnoreCase)) return _alice.Services;
            if (string.Equals(name, "bob", StringComparison.OrdinalIgnoreCase)) return _bob.Services;
            if (string.Equals(name, "charlie", StringComparison.OrdinalIgnoreCase)) return _charlie.Services;
            return null;
        }

        private sealed class ServerCallContextStub : ServerCallContext
        {
            private readonly string _peer;
            private readonly DateTime _deadline;
            private readonly Metadata _requestHeaders;
            private readonly CancellationToken _cancellationToken;

            public ServerCallContextStub(string peer, DateTime deadline, Metadata requestHeaders, CancellationToken cancellationToken)
            {
                _peer = peer;
                _deadline = deadline;
                _requestHeaders = requestHeaders;
                _cancellationToken = cancellationToken;
            }

            protected override string MethodCore => "/percolator.transport/DeliverOpaqueMessage";
            protected override string HostCore => "localhost";
            protected override string PeerCore => _peer;
            protected override DateTime DeadlineCore => _deadline;
            protected override Metadata RequestHeadersCore => _requestHeaders;
            protected override CancellationToken CancellationTokenCore => _cancellationToken;
            protected override Metadata ResponseTrailersCore { get; } = new Metadata();
            protected override Status StatusCore { get; set; }
            protected override WriteOptions WriteOptionsCore { get; set; }
            protected override AuthContext AuthContextCore { get; } = new AuthContext(null, new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<AuthProperty>>());
            protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) => null;
            protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
        }

        private sealed class MultiNodeLoopbackTransport : IMessageTransportService
        {
            private readonly IServiceProvider _senderProvider;
            private readonly Func<string, IServiceProvider?> _nameToProvider;
            private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, IServiceProvider> _sessionRoutes = new();
            public MultiNodeLoopbackTransport(IServiceProvider senderProvider, Func<string, IServiceProvider?> nameToProvider)
            {
                _senderProvider = senderProvider;
                _nameToProvider = nameToProvider;
            }

            public async Task<DeliverOpaqueMessageResponse> SendMessageAsync(
                Percolator.Identity.PeerId recipientPeerId,
                Percolator.Network.DirectSessionId directSessionId,
                Percolator.Cryptography.SessionRatchetMessage message,
                CancellationToken cancellationToken = default)
            {
                // Prefer cached route by session id (most accurate transport behavior)
                if (!_sessionRoutes.TryGetValue(directSessionId.Value, out var targetProvider))
                {
                    // Resolve recipient name on the SENDER node, then map name -> destination provider
                    var peerRepo = _senderProvider.GetRequiredService<Percolator.Identity.IPeerRepository>();
                    var peer = await peerRepo.GetByIdAsync(new Percolator.Identity.PeerId(recipientPeerId.Value));
                    if (peer is null)
                    {
                        TestContext.WriteLine($"[Loopback] Unknown recipient peer {recipientPeerId.Value} on sender; attempting by name lookup failed.");
                        throw new InvalidOperationException($"Unknown recipient peer {recipientPeerId.Value} on sender");
                    }
                    targetProvider = _nameToProvider(peer.Name)
                        ?? throw new InvalidOperationException($"No target provider found for peer name '{peer.Name}'");
                    _sessionRoutes[directSessionId.Value] = targetProvider;
                }
                // Create a PercolatorMessageService instance on the DESTINATION node
                var svc = ActivatorUtilities.CreateInstance<PercolatorMessageService>(targetProvider);
                var req = new DeliverOpaqueMessageRequest
                {
                    SessionId = directSessionId.Value.ToString(),
                    Payload = ByteString.CopyFrom(message.Value)
                };
                var ctx = new ServerCallContextStub(
                    peer: "ipv4:127.0.0.1:0",
                    deadline: DateTime.UtcNow.AddMinutes(1),
                    requestHeaders: new Metadata(),
                    cancellationToken: cancellationToken);
                TestContext.WriteLine($"[Loopback] DeliverOpaqueMessage to session={directSessionId.Value}...");
                var response = await svc.DeliverOpaqueMessage(req, ctx);
                return response;
            }
        }

        private IHost _host;
        private IHost _alice;
        private IHost _bob;
        private IHost _charlie;

        private int _hostPort, _alicePort, _bobPort, _charliePort;

        [SetUp]
        public override async Task SetUpAsync()
        {
            await base.SetUpAsync();

            // Start Host
            _hostPort = GetAvailablePort();
            _host = await CreateAndInitializeHostAsync(_hostPort, "GroupE2E-Host", identityName: "host", additionalServiceRegistration: services =>
            {
                services.AddPrekey();
            });

            // Build Alice with multi-node loopback (routes by recipient peer)
            _alicePort = GetAvailablePort();
            _alice = await CreateAndInitializeHostAsync(_alicePort, "GroupE2E-Alice", identityName: "alice", additionalServiceRegistration: services =>
            {
                services.AddPrekey();
                services.Replace(ServiceDescriptor.Singleton<IGrpcSessionService>(sp => new GrpcSessionLoopback(ResolveProviderByEndpoint)));
                services.Replace(ServiceDescriptor.Singleton<IMessageTransportService>(sp => new MultiNodeLoopbackTransport(sp, ResolveProviderByName)));
            });

            // Build Bob with multi-node loopback
            _bobPort = GetAvailablePort();
            _bob = await CreateAndInitializeHostAsync(_bobPort, "GroupE2E-Bob", identityName: "bob", additionalServiceRegistration: services =>
            {
                services.AddPrekey();
                services.Replace(ServiceDescriptor.Singleton<IGrpcSessionService>(sp => new GrpcSessionLoopback(ResolveProviderByEndpoint)));
                services.Replace(ServiceDescriptor.Singleton<IMessageTransportService>(sp => new MultiNodeLoopbackTransport(sp, ResolveProviderByName)));
            });

            // Build Charlie with multi-node loopback
            _charliePort = GetAvailablePort();
            _charlie = await CreateAndInitializeHostAsync(_charliePort, "GroupE2E-Charlie", identityName: "charlie", additionalServiceRegistration: services =>
            {
                services.AddPrekey();
                services.Replace(ServiceDescriptor.Singleton<IGrpcSessionService>(sp => new GrpcSessionLoopback(ResolveProviderByEndpoint)));
                services.Replace(ServiceDescriptor.Singleton<IMessageTransportService>(sp => new MultiNodeLoopbackTransport(sp, ResolveProviderByName)));
            });

            await _host.StartAsync();
            await _alice.StartAsync();
            await _bob.StartAsync();
            await _charlie.StartAsync();
        }

        [TearDown]
        public override async Task TearDownAsync()
        {
            if (_charlie != null) { await _charlie.StopAsync(); _charlie.Dispose(); }
            if (_bob != null) { await _bob.StopAsync(); _bob.Dispose(); }
            if (_alice != null) { await _alice.StopAsync(); _alice.Dispose(); }
            if (_host != null) { await _host.StopAsync(); _host.Dispose(); }
            await base.TearDownAsync();
        }

        [Test]
        public void Placeholder_Scaffolding_Compiles()
        {
            _host.Should().NotBeNull();
            _alice.Should().NotBeNull();
            _bob.Should().NotBeNull();
            _charlie.Should().NotBeNull();
        }

        [Test]
        public async Task Phase2_Prekeys_Dht_And_Sessions_Establish()
        {
            // Name each node on the others via public key (no peer-id sharing)
            await SetPeerNameOnNode(_alice, "host", GetIdentitySpki(_host));
            await SetPeerNameOnNode(_bob, "host", GetIdentitySpki(_host));
            await SetPeerNameOnNode(_charlie, "host", GetIdentitySpki(_host));

            await SetPeerNameOnNode(_host, "alice", GetIdentitySpki(_alice));
            await SetPeerNameOnNode(_host, "bob", GetIdentitySpki(_bob));
            await SetPeerNameOnNode(_host, "charlie", GetIdentitySpki(_charlie));

            // Also set cross-client names for P2P connections
            await SetPeerNameOnNode(_alice, "bob", GetIdentitySpki(_bob));
            await SetPeerNameOnNode(_alice, "charlie", GetIdentitySpki(_charlie));
            await SetPeerNameOnNode(_bob, "alice", GetIdentitySpki(_alice));
            await SetPeerNameOnNode(_bob, "charlie", GetIdentitySpki(_charlie));
            await SetPeerNameOnNode(_charlie, "alice", GetIdentitySpki(_alice));
            await SetPeerNameOnNode(_charlie, "bob", GetIdentitySpki(_bob));

            // Assert naming took effect
            await AssertPeerKnownByName(_alice, "host");
            await AssertPeerKnownByName(_bob, "host");
            await AssertPeerKnownByName(_charlie, "host");
            await AssertPeerKnownByName(_host, "alice");
            await AssertPeerKnownByName(_host, "bob");
            await AssertPeerKnownByName(_host, "charlie");
            await AssertPeerKnownByName(_alice, "bob");
            await AssertPeerKnownByName(_alice, "charlie");
            await AssertPeerKnownByName(_bob, "alice");
            await AssertPeerKnownByName(_bob, "charlie");
            await AssertPeerKnownByName(_charlie, "alice");
            await AssertPeerKnownByName(_charlie, "bob");

            // Establish direct sessions to Host first (needed for SubmitPreKeysHandler which sends via session)
            await ConnectToPeer(_alice, new DnsEndPoint("localhost", _hostPort), "host");
            await ConnectToPeer(_bob, new DnsEndPoint("localhost", _hostPort), "host");
            await ConnectToPeer(_charlie, new DnsEndPoint("localhost", _hostPort), "host");

            // Assert sessions exist on clients to host
            await AssertSessionExists(_alice, "host");
            await AssertSessionExists(_bob, "host");
            await AssertSessionExists(_charlie, "host");

            // Publish prekeys to Host
            TestContext.WriteLine("[Phase2] Submitting prekeys: Alice -> Host");
            (await SubmitPreKeys(_alice, targetPeerName: "host")).Should().Be(0);
            TestContext.WriteLine("[Phase2] Submitting prekeys: Bob -> Host");
            (await SubmitPreKeys(_bob, targetPeerName: "host")).Should().Be(0);
            TestContext.WriteLine("[Phase2] Submitting prekeys: Charlie -> Host");
            (await SubmitPreKeys(_charlie, targetPeerName: "host")).Should().Be(0);

            // DHT: probe host from clients and assert discovery improves after multiple probes
            var aliceMediator = _alice.Services.GetRequiredService<IMediator>();
            var bobMediator = _bob.Services.GetRequiredService<IMediator>();
            var hostEndpoint = new DnsEndPoint("localhost", _hostPort);

            var aliceProbe1 = await aliceMediator.Send(new DhtProbeCommand(hostEndpoint, "host", SelfIdentityName: null));
            var bobProbe = await bobMediator.Send(new DhtProbeCommand(hostEndpoint, "host", SelfIdentityName: null));
            var aliceProbe2 = await aliceMediator.Send(new DhtProbeCommand(hostEndpoint, "host", SelfIdentityName: null));

            aliceProbe1.Should().NotBeNull();
            bobProbe.Should().NotBeNull();
            aliceProbe2.Should().NotBeNull();
            aliceProbe2.CloserPeers.Count.Should().BeGreaterThanOrEqualTo(aliceProbe1.CloserPeers.Count);

            // Establish peer-to-peer sessions via host relay
            await ConnectToPeer(_bob, new DnsEndPoint("localhost", _alicePort), "alice");
            await ConnectToPeer(_charlie, new DnsEndPoint("localhost", _alicePort), "alice");
            await ConnectToPeer(_charlie, new DnsEndPoint("localhost", _bobPort), "bob");

            // Assert P2P sessions exist
            await AssertSessionExists(_bob, "alice");
            await AssertSessionExists(_charlie, "alice");
            await AssertSessionExists(_charlie, "bob");
        }

        private static byte[] GetIdentitySpki(IHost node)
        {
            var ctx = node.Services.GetRequiredService<ActiveIdentityContext>();
            if (ctx.Keys?.IdentitySigningKey is null) throw new InvalidOperationException("Identity keys not loaded");
            return ctx.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
        }

        private static async Task SetPeerNameOnNode(IHost node, string name, byte[] spki)
        {
            try
            {
                // Verify key store is available
                var keyStore = node.Services.GetService<Percolator.Identity.IPeerPublicSigningKeyStore>();
                TestContext.WriteLine($"[SetPeerName] IPeerPublicSigningKeyStore: {(keyStore == null ? "null" : keyStore.GetType().FullName)}");
                var mediator = node.Services.GetRequiredService<IMediator>();
                await mediator.Send(new SetPeerNameByPublicKeyCommand(name, spki));
                TestContext.WriteLine($"[SetPeerName] Set '{name}' ok.");
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"[SetPeerName] ERROR setting '{name}': {ex}");
                throw;
            }
        }

        private static async Task ConnectToPeer(IHost node, DnsEndPoint endpoint, string remoteName)
        {
            var mediator = node.Services.GetRequiredService<IMediator>();
            await mediator.Send(new ConnectToPeerCommand(endpoint, remoteName));
        }

        private static async Task<int> SubmitPreKeys(IHost node, string targetPeerName)
        {
            var mediator = node.Services.GetRequiredService<IMediator>();
            var expires = DateTimeOffset.UtcNow.AddHours(1);
            return await mediator.Send(new SubmitPreKeysCommand(targetPeerName, OneTimeKeyCount: 5, ExpiresUtc: expires));
        }

        private static async Task<Percolator.Network.DirectSessionId> EnsureDirectSessionAsync(IHost sender, string remoteName, DnsEndPoint endPoint)
        {
            var mediator = sender.Services.GetRequiredService<IMediator>();
            return await mediator.Send(new ConnectToPeerCommand(endPoint, remoteName));
        }

        private static async Task SendChatEnvelopeAsync(IHost sender, string recipientName, Percolator.Contracts.ChatEnvelope envelope, DnsEndPoint recipientEndpoint)
        {
            // Ensure session exists
            var sessionId = await EnsureDirectSessionAsync(sender, recipientName, recipientEndpoint);
            // Encrypt and send using sender's services
            var sessionManager = sender.Services.GetRequiredService<Percolator.Application.Sessions.IDirectSessionManager>();
            var transport = sender.Services.GetRequiredService<Percolator.Application.Network.IMessageTransportService>();
            var peerRepo = sender.Services.GetRequiredService<Percolator.Identity.IPeerRepository>();
            var peer = await peerRepo.GetByNameAsync(recipientName) ?? throw new InvalidOperationException($"Unknown peer name {recipientName}");
            var internalEnvelope = new Percolator.Contracts.InternalEnvelope { ChatEnvelope = envelope };
            var plaintext = new Percolator.Cryptography.Plaintext(internalEnvelope.ToByteArray());
            var cipher = await sessionManager.EncryptMessageAsync(new Percolator.Cryptography.SessionId(sessionId.Value), plaintext);
            await transport.SendMessageAsync(peer.Id, sessionId, cipher);
        }

        private static async Task AssertPeerKnownByName(IHost node, string name)
        {
            var repo = node.Services.GetRequiredService<Percolator.Identity.IPeerRepository>();
            var peer = await repo.GetByNameAsync(name);
            if (peer is null)
            {
                TestContext.WriteLine($"ASSERTION FAILED: Peer '{name}' not found on node.");
                Assert.Fail($"Peer '{name}' should exist on node but was not found.");
            }
        }

        private static async Task AssertSessionExists(IHost sender, string remoteName)
        {
            var convSvc = sender.Services.GetRequiredService<Percolator.Application.Sessions.IConversationService>();
            var repo = sender.Services.GetRequiredService<Percolator.Identity.IPeerRepository>();
            var peer = await repo.GetByNameAsync(remoteName) ?? throw new InvalidOperationException($"Peer '{remoteName}' not found on sender.");
            var existing = await convSvc.GetExistingDirectSessionAsync(peer);
            if (existing is null)
            {
                TestContext.WriteLine($"ASSERTION FAILED: No direct session found from sender to '{remoteName}'.");
                Assert.Fail($"Expected direct session from sender to '{remoteName}'.");
            }
        }

        private static AdminOperationPayload BuildUpdateGroupMembershipPayload(Guid groupGuid, Guid opId, Guid? add1 = null, Guid? add2 = null)
        {
            var payload = new AdminOperationPayload
            {
                Version = 1,
                GroupConversationGuid = ByteString.CopyFrom(groupGuid.ToByteArray()),
                OpId = ByteString.CopyFrom(opId.ToByteArray()),
                SentTimestampUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                UpdateGroupMembership = new UpdateGroupMembershipPayload { Version = 1 }
            };
            if (add1.HasValue)
                payload.UpdateGroupMembership.MembersToAdd.Add(ByteString.CopyFrom(add1.Value.ToByteArray()));
            if (add2.HasValue)
                payload.UpdateGroupMembership.MembersToAdd.Add(ByteString.CopyFrom(add2.Value.ToByteArray()));
            return payload;
        }

        private static byte[] SignCanonical(IHost signerNode, AdminOperationPayload payload)
        {
            var ctx = signerNode.Services.GetRequiredService<ActiveIdentityContext>();
            if (ctx.Keys?.IdentitySigningKey is null) throw new InvalidOperationException("Identity keys not loaded");
            var canonical = Percolator.Application.Apps.Chat.CanonicalPayload.ForAdminOperation(payload);
            using var ecdsa = ECDsa.Create(ctx.Keys.IdentitySigningKey.ExportParameters(true));
            return ecdsa.SignData(canonical, HashAlgorithmName.SHA256);
        }

        [Test]
        public async Task Phase3_GenesisGroupCreation_And_BaselineMessaging_Skeleton()
        {
            // Arrange: ensure names & sessions to host exist from Phase 2
            await Phase2_Prekeys_Dht_And_Sessions_Establish();

            // Resolve Bob and Charlie participant GUIDs on Alice
            var alicePeerRepo = _alice.Services.GetRequiredService<Percolator.Identity.IPeerRepository>();
            var bobPeer = await alicePeerRepo.GetByNameAsync("bob") ?? throw new InvalidOperationException("bob not known on alice");
            var charliePeer = await alicePeerRepo.GetByNameAsync("charlie") ?? throw new InvalidOperationException("charlie not known on alice");

            var groupGuid = Guid.NewGuid();
            var opId = Guid.NewGuid();
            var payload = BuildUpdateGroupMembershipPayload(groupGuid, opId, bobPeer.Id.Value, charliePeer.Id.Value);
            var signature = SignCanonical(_alice, payload);
            var sao = new SignedAdminOperation
            {
                Version = 1,
                Payload = payload,
                Signature = ByteString.CopyFrom(signature)
            };
            var chatEnv = new ChatEnvelope { SignedAdminOperation = sao };

            // Send to Bob and Charlie from Alice using existing sessions
            await SendChatEnvelopeAsync(_alice, "bob", chatEnv, new DnsEndPoint("localhost", _bobPort));
            await SendChatEnvelopeAsync(_alice, "charlie", chatEnv, new DnsEndPoint("localhost", _charliePort));

            // Assert group exists on Alice, Bob, Charlie with participants {Alice, Bob, Charlie}
            await AssertGroupParticipantsAsync(_alice, groupGuid, new[] { "alice", "bob", "charlie" });
            await AssertGroupParticipantsAsync(_bob, groupGuid, new[] { "alice", "bob", "charlie" });
            await AssertGroupParticipantsAsync(_charlie, groupGuid, new[] { "alice", "bob", "charlie" });
        }

        private static async Task AssertGroupParticipantsAsync(IHost node, Guid groupGuid, string[] expectedNames)
        {
            var ctx = node.Services.GetRequiredService<ActiveIdentityContext>();
            var selfId = ctx.Identity?.SelfIdentityId ?? throw new InvalidOperationException("SelfIdentityId not loaded");
            var repo = node.Services.GetRequiredService<Percolator.Chat.IConversationRepository>();
            var convo = await repo.GetByGroupGuidAsync(groupGuid, selfId);
            if (convo is null)
            {
                Assert.Fail($"Expected group conversation {groupGuid} to exist on node, but it was not found.");
            }
            var peerRepo = node.Services.GetRequiredService<Percolator.Identity.IPeerRepository>();
            var expected = new HashSet<Guid>();
            foreach (var name in expectedNames)
            {
                var p = await peerRepo.GetByNameAsync(name) ?? throw new InvalidOperationException($"Peer '{name}' not known on node");
                expected.Add(p.Id.Value);
            }
            var actual = convo.Participants.Select(p => p.Value).ToHashSet();
            if (!expected.IsSubsetOf(actual))
            {
                TestContext.WriteLine($"ASSERTION FAILED: Group participants mismatch. Expected subset: {string.Join(",", expected)} Actual: {string.Join(",", actual)}");
                Assert.Fail("Group participants mismatch.");
            }
        }
    }
}
