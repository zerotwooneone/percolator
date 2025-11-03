using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Microsoft.Extensions.Logging.Abstractions;
using Percolator.Application.Apps.Chat;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Contracts;
using Percolator.Chat.App;
using Percolator.Identity;
using Percolator.Application.Network;

    namespace Percolator.ApplicationTests.Chat
    {
        public sealed class CreateGroupTests
        {
            private static byte[] Spki(params byte[] bytes) => bytes;

        private sealed class CapturingMediator : MediatR.IMediator
        {
            public object? LastRequest { get; private set; }

internal sealed class NoopSender : IRemoteEnvelopeSender
{
    public Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, RecipientRoute recipient, CancellationToken ct = default)
        => Task.CompletedTask;
}
            public Task<TResponse> Send<TResponse>(MediatR.IRequest<TResponse> request, CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                return Task.FromResult(default(TResponse)!);
            }
            public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : MediatR.IRequest
            {
                LastRequest = request!;
                return Task.CompletedTask;
            }
            public Task<object?> Send(object request, CancellationToken cancellationToken = default)
            {
                LastRequest = request;
                return Task.FromResult<object?>(null);
            }
            public async IAsyncEnumerable<TResponse> CreateStream<TResponse>(MediatR.IStreamRequest<TResponse> request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield break;
            }
            public async IAsyncEnumerable<object?> CreateStream(object request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                yield break;
            }
            public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : MediatR.INotification => Task.CompletedTask;
        }
            private sealed class StubKeyStore : IPeerPublicSigningKeyStore
            {
                private readonly Dictionary<string, PeerId> _map = new();
                public void Add(byte[] spki, PeerId id)
                {
                    var pkh = SHA256.HashData(spki);
                    _map[Convert.ToBase64String(pkh)] = id;
                }
                public Task ActivateIfChangedAsync(PeerId peerId, byte[] publicKeySpki, byte[] publicKeyHash, DateTimeOffset nowUtc, CancellationToken ct = default) => Task.CompletedTask;
                public Task<PeerId?> GetPeerIdByPublicKeyHashAsync(byte[] publicKeyHash, CancellationToken ct = default)
                {
                    _map.TryGetValue(Convert.ToBase64String(publicKeyHash), out var id);
                    return Task.FromResult<PeerId?>(id);
                }
                public Task<byte[]?> GetPublicKeyHashByPeerIdAsync(PeerId peerId, CancellationToken cancellationToken)
                {
                    foreach (var kvp in _map)
                    {
                        if (kvp.Value.Equals(peerId))
                        {
                            return Task.FromResult<byte[]?>(Convert.FromBase64String(kvp.Key));
                        }
                    }
                    return Task.FromResult<byte[]?>(null);
                }
            }

            private sealed class NoopAdminKeyStore : IGroupAdminKeyStore
            {
                public Task<IReadOnlyList<GroupAdminKeyRecord>> GetKeysAsync(Guid conversationId, CancellationToken ct) => Task.FromResult<IReadOnlyList<GroupAdminKeyRecord>>(Array.Empty<GroupAdminKeyRecord>());
                public Task AddKeyAsync(Guid conversationId, AdminPublicKey publicKey, DateTimeOffset addedAtUtc, CancellationToken ct) => Task.CompletedTask;
                public Task RevokeKeyAsync(Guid conversationId, AdminPublicKey publicKey, DateTimeOffset revokedAtUtc, CancellationToken ct) => Task.CompletedTask;
            }

        private sealed class CapturingConversationRepo : IConversationRepository
        {
            public (Guid groupGuid, int selfId, List<ParticipantId> participants, string? name)? LastCreate;
            public Task CreateGroupAsync(Guid groupConversationGuid, int selfIdentityId, IEnumerable<ParticipantId> initialParticipants, string? name)
            {
                LastCreate = (groupConversationGuid, selfIdentityId, initialParticipants.ToList(), name);
                return Task.CompletedTask;
            }
            public Task<Conversation?> GetByIdAsync(ConversationId id, int selfIdentityId) => Task.FromResult<Conversation?>(null);
            public Task AddAsync(Conversation conversation, int selfIdentityId) => Task.CompletedTask;
            public Task UpdateAsync(Conversation conversation, int selfIdentityId) => Task.CompletedTask;
            public Task<Conversation?> GetByGroupGuidAsync(Guid groupConversationGuid, int selfIdentityId) => Task.FromResult<Conversation?>(null);
            public Task<Conversation?> GetByParticipantPairAsync(int selfIdentityId, Guid otherPeerId) => Task.FromResult<Conversation?>(null);
            public Task UpsertDirectSessionMappingAsync(int selfIdentityId, Guid directSessionId, ConversationId conversationId) => Task.CompletedTask;
        }

        [Test]
        public async Task ProcessInternalEnvelope_CreateGroup_DispatchesCommandWithSelfIdentity()
        {
            var mediator = new CapturingMediator();
            var handler = new ProcessInternalEnvelopeHandler(
                new NullLogger<ProcessInternalEnvelopeHandler>(),
                mediator,
                new Moq.Mock<Percolator.Chat.App.IAdminOperations>().Object,
                new Moq.Mock<Percolator.Dht.IDhtService>().Object,
                new Moq.Mock<Percolator.Network.IPeerConnectionRepository>().Object,
                new Moq.Mock<Percolator.MessageQueue.Abstractions.IMessageQueueService>().Object);

            var groupGuid = Guid.NewGuid();
            var cg = new CreateGroup
            {
                Version = 1,
                GroupConversationGuid = Google.Protobuf.ByteString.CopyFrom(groupGuid.ToByteArray())
            };
            cg.InitialParticipantIdentityKeys.Add(Google.Protobuf.ByteString.CopyFrom(Spki(1)));
            cg.InitialParticipantIdentityKeys.Add(Google.Protobuf.ByteString.CopyFrom(Spki(2)));
            cg.InitialParticipantIdentityKeys.Add(Google.Protobuf.ByteString.CopyFrom(Spki(3)));
            cg.CreatorIdentityKey = Google.Protobuf.ByteString.CopyFrom(Spki(1));

            var env = new InternalEnvelope { ChatEnvelope = new ChatEnvelope { CreateGroup = cg } };
            var ctx = new SessionContext(SessionId: Guid.NewGuid(), SelfIdentityId: 123, RemotePeerGuid: Guid.NewGuid());

            await handler.Handle(new ProcessInternalEnvelopeCommand(env, ctx), CancellationToken.None);

            var sent = mediator.LastRequest as CreateGroupFromIdentityKeysCommand;
            Assert.That(sent, Is.Not.Null, "Expected CreateGroupFromIdentityKeysCommand to be sent");
            Assert.That(sent!.SelfIdentityId, Is.EqualTo(123));
            Assert.That(sent.GroupConversationGuid, Is.EqualTo(groupGuid));
            Assert.That(sent.ParticipantIdentityKeysSpki.Count, Is.EqualTo(3));
            Assert.That(sent.CreatorIdentityKeySpki, Is.Not.Null);
        }

        [Test]
        public async Task CreateGroupHandler_Aborts_When_Participants_Less_Than_Two()
        {
            var store = new StubKeyStore();
            var repo = new CapturingConversationRepo();
            var handler = new CreateGroupFromIdentityKeysHandler(new NullLogger<CreateGroupFromIdentityKeysHandler>(), store, repo, new NoopAdminKeyStore(), new NoopSender());

            var groupGuid = Guid.NewGuid();
            var selfId = 77;
            var spkiA = Spki(10);
            // Only one SPKI mapped
            store.Add(spkiA, PeerId.NewId());

            await handler.Handle(new CreateGroupFromIdentityKeysCommand(selfId, groupGuid, new List<byte[]> { spkiA }, "test", spkiA), CancellationToken.None);

            Assert.That(repo.LastCreate, Is.Null, "Should not create group when < 2 participants resolved");
        }

        [Test]
        public async Task CreateGroupHandler_Creates_When_TwoOrMoreParticipants()
        {
            var store = new StubKeyStore();
            var repo = new CapturingConversationRepo();
            var handler = new CreateGroupFromIdentityKeysHandler(new NullLogger<CreateGroupFromIdentityKeysHandler>(), store, repo, new NoopAdminKeyStore(), new NoopSender());

            var groupGuid = Guid.NewGuid();
            var selfId = 88;
            var spkiA = Spki(10);
            var spkiB = Spki(20);
            store.Add(spkiA, PeerId.NewId());
            store.Add(spkiB, PeerId.NewId());

            await handler.Handle(new CreateGroupFromIdentityKeysCommand(selfId, groupGuid, new List<byte[]> { spkiA, spkiB }, "ok", spkiA), CancellationToken.None);

            Assert.That(repo.LastCreate, Is.Not.Null, "Expected group to be created");
            Assert.That(repo.LastCreate!.Value.selfId, Is.EqualTo(selfId));
            Assert.That(repo.LastCreate!.Value.groupGuid, Is.EqualTo(groupGuid));
            Assert.That(repo.LastCreate!.Value.participants.Count, Is.GreaterThanOrEqualTo(2));
        }
    }
}

    internal sealed class NoopSender : IRemoteEnvelopeSender
    {
        public Task SendChatEnvelopeToPeerAsync(ChatEnvelope chatEnvelope, RecipientRoute recipient, CancellationToken ct = default)
            => Task.CompletedTask;
    }
