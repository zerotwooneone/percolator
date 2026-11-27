namespace Percolator.ApplicationIntegrationTests.Harness;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application.Channels.Events;
using Percolator.Application.Connections;
using Percolator.Application.Delivery;
using Percolator.Application.Delivery.Events;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Invitations;
using Percolator.Application.Ports;
using Percolator.Application.Profiles;
using Percolator.Application.Profiles.Events;
using Percolator.Application.Session.Events;
using Percolator.Application.Session.Ports;
using Percolator.Apps.Chat;
using Percolator.Apps.Chat.DependencyInjection;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Profiles;
using Percolator.Apps.Chat.Ingress;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.DependencyInjection;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Transport.Workers;
using Percolator.Infrastructure.Vault;
using Percolator.PluginSdk;
using Percolator.PluginSdk.Events;
using Percolator.PluginSdk.Profiles;
using PluginDeliveryRoute = Percolator.PluginSdk.DeliveryRoute;

public sealed class TestPersonaNode : IAsyncDisposable
{
    private readonly SqliteConnection _keeperConnection;
    private readonly ServiceProvider _serviceProvider;
    private readonly List<IHostedService> _hostedServices = new();
    private readonly MasterKeyHandle _masterKeyHandle;
    private CancellationTokenSource _drainCts = new();
    private Task? _drainLoopTask;

    public PublicIdentityId IdentityId { get; }
    public DeviceId DeviceId { get; } = DeviceId.Primary;
    public string DisplayName { get; }
    public IServiceProvider Services => _serviceProvider;

    private TestPersonaNode(
        SqliteConnection keeperConnection,
        ServiceProvider serviceProvider,
        MasterKeyHandle masterKeyHandle,
        PublicIdentityId identityId,
        string displayName)
    {
        _keeperConnection = keeperConnection;
        _serviceProvider = serviceProvider;
        _masterKeyHandle = masterKeyHandle;
        IdentityId = identityId;
        DisplayName = displayName;
    }

    public static async Task<TestPersonaNode> CreateAsync(string displayName = "Persona")
    {
        SQLitePCL.Batteries_V2.Init();

        var personaId = PublicIdentityId.New();
        var connectionString = $"DataSource=file:persona_{personaId.Value:N}?mode=memory&cache=shared";
        var keeperConnection = new SqliteConnection(connectionString);
        await keeperConnection.OpenAsync().ConfigureAwait(false);

        var masterKey = MasterKeyHandle.GenerateRandom();
        var storageOptions = new VaultStorageOptions
        {
            UseEncryption = false
        };

        var services = new ServiceCollection();
        services.AddLogging(lb =>
        {
            lb.AddConsole();
            lb.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSessionCoreInfrastructure(masterKey, storageOptions);
        services.AddVaultPersistenceInfrastructure();

        // Direct in-memory SQLite keeper database override
        services.AddDbContextFactory<PercolatorDbContext>((sp, dbOptions) =>
        {
            dbOptions.UseSqlite(connectionString);
        });
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<PercolatorDbContext>>().CreateDbContext());

        services.AddTransportInfrastructure();

        // Fast poll for deterministic outbox testing
        services.AddSingleton(new OutboxDispatchHostOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            BatchSize = 50
        });

        services.AddBlobStorageInfrastructure();
        services.AddChatInfrastructure();
        services.AddChatApp();
        services.AddFileTransferInfrastructure();

        services.AddScoped<IOutboundConnectionWorkflow, OutboundConnectionWorkflow>();
        services.AddScoped<IInboundConnectionWorkflow, InboundConnectionWorkflow>();
        services.AddScoped<IPeerInvitationService, PeerInvitationService>();

        // Decorate IChatMessageRepository to notify ChatMessageReceivedEvent
        services.AddScoped<SqlChatMessageRepository>();
        services.AddScoped<IChatMessageRepository>(sp =>
            new NotifyingChatMessageRepository(
                sp.GetRequiredService<SqlChatMessageRepository>(),
                sp.GetRequiredService<IEventPublisher>()));

        var serviceProvider = services.BuildServiceProvider();

        // Ensure database schema is created
        using (var scope = serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PercolatorDbContext>();
            await db.Database.EnsureCreatedAsync().ConfigureAwait(false);

            var timeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
            var profileRepo = scope.ServiceProvider.GetRequiredService<IIdentityProfileRepository>();
            var profile = IdentityProfile.Create(personaId, displayName, IdentityRole.UserPersona, timeProvider).Value!;
            await profileRepo.SaveAsync(profile).ConfigureAwait(false);

            var cryptoEngine = scope.ServiceProvider.GetRequiredService<ICryptoEngine>();
            var (priv, pub) = cryptoEngine.GenerateEphemeralKeyPair();
            using var profileKey = ProfileKey.Generate();
            var idKeyStore = scope.ServiceProvider.GetRequiredService<ILocalIdentityKeyStore>();
            await idKeyStore.SaveIdentityKeysAsync(personaId, priv, IdentityKey.FromSpan(pub.Span), profileKey).ConfigureAwait(false);

            var (spkPriv, spkPub) = cryptoEngine.GenerateEphemeralKeyPair();
            var spkSig = cryptoEngine.SignEd25519(priv.Span, spkPub.Span);
            var preKeyStore = scope.ServiceProvider.GetRequiredService<IPrivatePreKeyStore>();
            await preKeyStore.StoreSignedPreKeyAsync(personaId, DeviceId.Primary, spkPriv, spkPub, DeviceLinkProof.FromSpan(spkSig)).ConfigureAwait(false);

            var activeSet = scope.ServiceProvider.GetRequiredService<IActiveIdentitySet>();
            await activeSet.AddAsync(personaId).ConfigureAwait(false);
        }

        var node = new TestPersonaNode(keeperConnection, serviceProvider, masterKey, personaId, displayName);

        // Start background workers
        var hostedServices = serviceProvider.GetServices<IHostedService>().ToList();
        foreach (var svc in hostedServices)
        {
            if (svc is not RelayMailboxDrainHost)
            {
                await svc.StartAsync(CancellationToken.None).ConfigureAwait(false);
                node._hostedServices.Add(svc);
            }
        }

        // Start DrainScheduler under node-managed supervisor loop
        var scheduler = serviceProvider.GetRequiredService<DrainScheduler>();
        node._drainLoopTask = Task.Run(() => scheduler.RunAsync(node._drainCts.Token));

        return node;
    }

    public async Task LinkRelayAsync(TestRelayServer relay)
    {
        var relayDir = Services.GetRequiredService<IRelayDirectory>();
        var reg = await relayDir.RegisterRemoteRelayAsync(relay.RelayIdentityId, relay.Endpoint).ConfigureAwait(false);
        if (reg.IsFailure) throw new InvalidOperationException($"RegisterRemoteRelay failed: {reg.Error.Code} {reg.Error.Description}");

        var link = await relayDir.LinkIdentityAsync(relay.RelayIdentityId, IdentityId).ConfigureAwait(false);
        if (link.IsFailure) throw new InvalidOperationException($"LinkIdentity failed: {link.Error.Code} {link.Error.Description}");

        var prov = await relayDir.EnsureMailboxProvisionedAsync(relay.RelayIdentityId, IdentityId).ConfigureAwait(false);
        if (prov.IsFailure) throw new InvalidOperationException($"EnsureMailboxProvisioned failed: {prov.Error.Code} {prov.Error.Description}");

        var preKeyService = Services.GetRequiredService<IPreKeyMaintenanceService>();
        var target = new MailboxDrainTarget(IdentityId, relay.RelayIdentityId, relay.Endpoint);
        await preKeyService.EvaluateAsync(target, forced: true).ConfigureAwait(false);
    }

    public async Task<BlindedRoutingToken> GetRoutingTokenAsync(TestRelayServer relay)
    {
        var relayDir = Services.GetRequiredService<IRelayDirectory>();
        var tokens = await relayDir.GetMailboxTokensAsync(IdentityId, relay.RelayIdentityId).ConfigureAwait(false);
        if (!tokens.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to get mailbox tokens: {tokens.Error.Description}");
        }
        return tokens.Value!.RoutingToken;
    }

    public async Task<string> CreateInvitationAsync(TestRelayServer relay)
    {
        var invService = Services.GetRequiredService<IPeerInvitationService>();
        var invResult = await invService.CreatePeerInvitationAsync(IdentityId, relay.Endpoint).ConfigureAwait(false);
        if (invResult.IsFailure)
        {
            throw new InvalidOperationException($"Create invitation failed: {invResult.Error.Code} {invResult.Error.Description}");
        }
        var codec = Services.GetRequiredService<IPeerInvitationCodec>();
        return codec.Encode(PeerInvitationPayload.FromDomain(invResult.Value!));
    }

    public async Task<ChannelId> ConnectWithInvitationAsync(string invitationUri)
    {
        var workflow = Services.GetRequiredService<IOutboundConnectionWorkflow>();
        var codec = Services.GetRequiredService<IPeerInvitationCodec>();
        var decoded = codec.Decode(invitationUri);
        if (!decoded.IsSuccess)
        {
            throw new InvalidOperationException($"Decode invitation failed: {decoded.Error.Description}");
        }
        var payload = decoded.Value!;
        var result = await workflow.ConnectAsync(IdentityId, payload, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Connect with invitation failed: {result.Error.Code} {result.Error.Description}");
        }
        return ChannelId.DeterministicForDirect(IdentityId, result.Value!.PeerId, payload.RelayEndpoint);
    }

    public async Task<DomainResult<OutboundConnectionResult>> TryConnectWithInvitationPayloadAsync(
        PeerInvitationPayload payload,
        TimeSpan timeout)
    {
        using var scope = Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IOutboundConnectionWorkflow>();
        return await workflow.ConnectAsync(IdentityId, payload, timeout).ConfigureAwait(false);
    }

    public async Task RotateSignedPreKeyAsync(TestRelayServer relay)
    {
        using var scope = Services.CreateScope();
        var preKeyStore = scope.ServiceProvider.GetRequiredService<IPrivatePreKeyStore>();
        var idKeyStore = scope.ServiceProvider.GetRequiredService<ILocalIdentityKeyStore>();
        var crypto = scope.ServiceProvider.GetRequiredService<ICryptoEngine>();
        var provisioner = scope.ServiceProvider.GetRequiredService<IRelayMailboxProvisioner>();
        var relayDir = scope.ServiceProvider.GetRequiredService<IRelayDirectory>();

        using var idPriv = await idKeyStore.GetIdentityPrivateKeyAsync(IdentityId).ConfigureAwait(false);
        var (spkPriv, spkPub) = crypto.GenerateEphemeralKeyPair();
        try
        {
            var sig = DeviceLinkProof.FromSpan(crypto.SignEd25519(idPriv!.Span, spkPub.Span));
            await preKeyStore.StoreSignedPreKeyAsync(IdentityId, DeviceId.Primary, spkPriv, spkPub, sig).ConfigureAwait(false);
        }
        finally
        {
            spkPriv.Dispose();
        }

        var tokens = (await relayDir.GetMailboxTokensAsync(IdentityId, relay.RelayIdentityId).ConfigureAwait(false)).Value!;
        await provisioner.PublishPreKeyBundleAsync(IdentityId, DeviceId.Primary, relay.Endpoint, tokens, 0).ConfigureAwait(false);
    }

    public async Task ApproveContactAsync(PublicIdentityId peerId)
    {
        using var scope = Services.CreateScope();
        var workflow = scope.ServiceProvider.GetRequiredService<IInboundConnectionWorkflow>();
        var result = await workflow.AcceptRequestAsync(IdentityId, peerId).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Approve contact failed: {result.Error.Code} {result.Error.Description}");
        }
    }

    public async Task SendProfileCapabilityAsync(ChannelId channelId, PublicIdentityId recipientId)
    {
        using var scope = Services.CreateScope();
        var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();
        var idKeyStore = scope.ServiceProvider.GetRequiredService<ILocalIdentityKeyStore>();
        var relayDir = scope.ServiceProvider.GetRequiredService<IRelayDirectory>();
        var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();

        var directChannel = await channelRepo.GetDirectByIdAsync(channelId).ConfigureAwait(false);
        var relayEndpoint = directChannel?.RelayEndpoint;
        if (relayEndpoint == null)
        {
            throw new InvalidOperationException("Channel has no RelayEndpoint.");
        }

        var relayList = await relayDir.ListAsync().ConfigureAwait(false);
        var matchingRelay = relayList.FirstOrDefault(r => r.Endpoint == relayEndpoint);
        if (matchingRelay == null)
        {
            throw new InvalidOperationException($"No linked relay matches endpoint {relayEndpoint.Value}");
        }

        var triadResult = await relayDir.GetMailboxTokensAsync(IdentityId, matchingRelay.RelayIdentityId).ConfigureAwait(false);
        if (triadResult.IsFailure)
        {
            throw new InvalidOperationException($"Failed to get mailbox tokens: {triadResult.Error.Description}");
        }

        var profileKey = await idKeyStore.GetProfileKeyAsync(IdentityId).ConfigureAwait(false);
        var pubKey = await idKeyStore.GetIdentityPublicKeyAsync(IdentityId).ConfigureAwait(false);
        var cryptoEngine = scope.ServiceProvider.GetRequiredService<ICryptoEngine>();
        var blindedLocator = BlindedProfileLocator.Derive(pubKey!, profileKey!, cryptoEngine);

        var cmd = new ShareProfileCapabilityCommand(
            channelId,
            IdentityId,
            recipientId,
            DeviceId.Primary,
            profileKey!,
            blindedLocator,
            triadResult.Value!.RoutingToken,
            1u,
            PluginDeliveryRoute.RelayedOneToOne,
            AuthorPublicKey: pubKey,
            SourceEndpoint: relayEndpoint.Value,
            SourceType: ProfileSourceType.Relay);

        var shareResult = await chatService.ShareProfileCapabilityAsync(cmd).ConfigureAwait(false);
        if (!shareResult.IsSuccess)
        {
            throw new InvalidOperationException($"Share profile capability failed: {shareResult.Error.Code} {shareResult.Error.Description}");
        }
    }

    public async Task SendChatAsync(ChannelId channelId, string text, PublicIdentityId? recipientId = null)
    {
        using var scope = Services.CreateScope();
        var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();
        var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
        var directChannel = await channelRepo.GetDirectByIdAsync(channelId).ConfigureAwait(false);
        var targetPeerId = recipientId ?? directChannel?.RemotePeerId;
        var cmd = new SendMessageCommand(
            channelId,
            IdentityId,
            targetPeerId,
            DeviceId.Primary,
            directChannel != null ? PluginDeliveryRoute.RelayedOneToOne : PluginDeliveryRoute.RelayedGroup,
            text);

        var result = await chatService.SendMessageAsync(cmd).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException($"Send chat message failed: {result.Error.Code} {result.Error.Description}");
        }
    }

    public async Task<ChannelId> CreateGroupAsync(TestRelayServer relay, string groupName, IReadOnlyList<TestPersonaNode> members)
    {
        using var scope = Services.CreateScope();
        var channelFactory = scope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
        var grpcChannel = channelFactory.GetOrCreateChannel(relay.Endpoint.ToUri());
        var client = new AnonymousGroupRelayService.AnonymousGroupRelayServiceClient(grpcChannel);

        var channelId = ChannelId.New();
        var createReq = new CreateGroupRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            EncryptedGenesisBlob = ByteString.CopyFrom(new byte[128]),
            ZkPublicParams = ByteString.CopyFrom(new byte[64])
        };

        var myToken = await GetRoutingTokenAsync(relay).ConfigureAwait(false);
        createReq.InitialRoutingTokens.Add(ByteString.CopyFrom(myToken.ToByteArray()));

        foreach (var member in members)
        {
            if (member.IdentityId != IdentityId)
            {
                var memberToken = await member.GetRoutingTokenAsync(relay).ConfigureAwait(false);
                createReq.InitialRoutingTokens.Add(ByteString.CopyFrom(memberToken.ToByteArray()));
            }
        }

        var createResp = await client.CreateGroupAsync(createReq).ConfigureAwait(false);
        if (!createResp.Created)
        {
            throw new InvalidOperationException($"Relay rejected group creation for {channelId}");
        }

        var credRepo = scope.ServiceProvider.GetRequiredService<IGroupCredentialsRepository>();
        var cred = GroupCredentials.CreateGenesis(
            channelId,
            GroupMasterKey.FromSpan(new byte[32]),
            AuthCredentialMacBytes.FromSpan(new byte[64])).Value!;
        await credRepo.SaveAsync(cred).ConfigureAwait(false);

        var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
        var timeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var groupChannel = GroupChannel.CreateGenesis(channelId, IdentityId, groupName, timeProvider, relay.Endpoint).Value!;
        foreach (var member in members)
        {
            if (member.IdentityId != IdentityId)
            {
                groupChannel.AddMember(IdentityId, member.IdentityId, ChannelRole.Member, timeProvider);
            }
        }
        await channelRepo.SaveGroupAsync(groupChannel).ConfigureAwait(false);

        var keyDist = scope.ServiceProvider.GetRequiredService<IGroupKeyDistributionService>();
        var otherMemberIds = members.Where(m => m.IdentityId != IdentityId).Select(m => m.IdentityId).ToList();
        var distResult = await keyDist.DistributeSenderKeyAsync(channelId, IdentityId, DeviceId.Primary, otherMemberIds).ConfigureAwait(false);
        if (distResult.IsFailure)
        {
            throw new InvalidOperationException($"Distribute sender key failed: {distResult.Error!.Description}");
        }

        return channelId;
    }

    public async Task JoinGroupAsync(TestRelayServer relay, ChannelId channelId, string groupName, PublicIdentityId adminId, IReadOnlyList<PublicIdentityId> allMembers)
    {
        using var scope = Services.CreateScope();
        var credRepo = scope.ServiceProvider.GetRequiredService<IGroupCredentialsRepository>();
        var cred = GroupCredentials.CreateGenesis(
            channelId,
            GroupMasterKey.FromSpan(new byte[32]),
            AuthCredentialMacBytes.FromSpan(new byte[64])).Value!;
        await credRepo.SaveAsync(cred).ConfigureAwait(false);

        var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
        var timeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var groupChannel = GroupChannel.CreateGenesis(channelId, adminId, groupName, timeProvider, relay.Endpoint).Value!;
        foreach (var memberId in allMembers)
        {
            if (memberId != adminId)
            {
                groupChannel.AddMember(adminId, memberId, ChannelRole.Member, timeProvider);
            }
        }
        await channelRepo.SaveGroupAsync(groupChannel).ConfigureAwait(false);

        var keyDist = scope.ServiceProvider.GetRequiredService<IGroupKeyDistributionService>();
        var otherMemberIds = allMembers.Where(m => m != IdentityId).ToList();
        var distResult = await keyDist.DistributeSenderKeyAsync(channelId, IdentityId, DeviceId.Primary, otherMemberIds).ConfigureAwait(false);
        if (distResult.IsFailure)
        {
            throw new InvalidOperationException($"Distribute sender key failed: {distResult.Error!.Description}");
        }
    }

    public async Task WaitForGroupReceiverSessionAsync(ChannelId channelId, PublicIdentityId authorId, TimeSpan timeout, uint minKeyId = 1)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (!cts.IsCancellationRequested)
        {
            using var scope = Services.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IGroupReceiverSessionRepository>();
            var session = await repo.GetReceiverSessionAsync(channelId, authorId, DeviceId.Primary).ConfigureAwait(false);
            if (session != null && session.KeyId >= minKeyId) return;
            try
            {
                if (!await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) { break; }
        }
        throw new TimeoutException($"Timed out waiting for group receiver session (keyId >= {minKeyId}) for author {authorId.Value}");
    }

    public async Task WaitForOutboxRetryAsync(TimeSpan timeout, int minRetryCount = 1)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (!cts.IsCancellationRequested)
        {
            using var scope = Services.CreateScope();
            var outboxRepo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
            var jobs = await outboxRepo.GetPendingJobsForOwnerAsync(IdentityId, cts.Token).ConfigureAwait(false);
            if (jobs.Any(j => j.RetryCount >= minRetryCount))
            {
                return;
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException) { break; }
        }
        throw new TimeoutException($"Timed out waiting for outbox retry count >= {minRetryCount}");
    }

    public async Task EvictGroupMemberAsync(TestRelayServer relay, ChannelId channelId, TestPersonaNode memberToEvict, IReadOnlyList<TestPersonaNode> remainingMembers)
    {
        using var scope = Services.CreateScope();
        var channelFactory = scope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
        var grpcChannel = channelFactory.GetOrCreateChannel(relay.Endpoint.ToUri());
        var client = new AnonymousGroupRelayService.AnonymousGroupRelayServiceClient(grpcChannel);

        var zkEngine = scope.ServiceProvider.GetRequiredService<IZkProofEngine>();
        var credRepo = scope.ServiceProvider.GetRequiredService<IGroupCredentialsRepository>();
        var cred = await credRepo.GetAsync(channelId).ConfigureAwait(false);
        uint currentEpoch = cred?.CurrentEpoch.Value ?? 0;

        byte[] readChallenge = new byte[32];
        RelayGroupLedger.ComputeReadChallenge(channelId, new EpochNumber(currentEpoch), readChallenge);
        var readProof = zkEngine.GenerateGroupPresentation(currentEpoch, readChallenge, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        var stateResponse = await client.FetchGroupStateAsync(new FetchGroupStateRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            ZkPresentationProof = ByteString.CopyFrom(readProof.Span)
        }).ConfigureAwait(false);

        var remainingTokens = new HashSet<BlindedRoutingToken>();
        var myToken = await GetRoutingTokenAsync(relay).ConfigureAwait(false);
        remainingTokens.Add(myToken);
        foreach (var m in remainingMembers)
        {
            if (m.IdentityId != IdentityId && m.IdentityId != memberToEvict.IdentityId)
            {
                remainingTokens.Add(await m.GetRoutingTokenAsync(relay).ConfigureAwait(false));
            }
        }

        Span<byte> challenge = stackalloc byte[32];
        var newBlob = EncryptedEntriesBlob.FromSpan(stateResponse.EncryptedRosterBlob.Span);
        RelayGroupLedger.ComputeMutationChallenge(newBlob, remainingTokens, challenge);
        var proof = zkEngine.GenerateGroupPresentation(stateResponse.CurrentEpoch, challenge, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

        var mutationReq = new CommitRosterMutationRequest
        {
            ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
            BaseEpoch = stateResponse.CurrentEpoch,
            NewRosterBlob = stateResponse.EncryptedRosterBlob,
            ZkPresentationProof = ByteString.CopyFrom(proof.Span)
        };

        foreach (var token in remainingTokens)
        {
            mutationReq.NewRoutingTokens.Add(ByteString.CopyFrom(token.ToByteArray()));
        }

        var response = await client.CommitRosterMutationAsync(mutationReq).ConfigureAwait(false);
        if (!response.Committed)
        {
            throw new InvalidOperationException($"Commit roster mutation rejected by relay for {channelId}");
        }

        if (cred != null)
        {
            cred.UpdateEpoch(new EpochNumber(response.NewEpoch));
            await credRepo.SaveAsync(cred).ConfigureAwait(false);
        }

        var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
        var timeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var groupChannel = await channelRepo.GetGroupByIdAsync(channelId).ConfigureAwait(false);
        if (groupChannel != null)
        {
            groupChannel.RemoveMember(IdentityId, memberToEvict.IdentityId, timeProvider);
            await channelRepo.SaveGroupAsync(groupChannel).ConfigureAwait(false);
        }

        // Rotate Alice's sender key
        var senderKeyRepo = scope.ServiceProvider.GetRequiredService<IGroupSenderKeyRepository>();
        var oldRatchet = await senderKeyRepo.GetSenderKeyRatchetAsync(channelId, IdentityId, DeviceId.Primary).ConfigureAwait(false);
        uint nextKeyId = oldRatchet != null ? oldRatchet.KeyId + 1 : 1;

        var crypto = scope.ServiceProvider.GetRequiredService<ICryptoEngine>();
        var (newSigningPriv, newSigningPub) = crypto.GenerateEphemeralKeyPair();
        var chainBytes = new byte[32];
        RandomNumberGenerator.Fill(chainBytes);
        using var newChain = ChainKey.FromSpan(chainBytes);

        var newRatchet = new GroupSenderKeyRatchet(
            channelId,
            IdentityId,
            DeviceId.Primary,
            newChain,
            initialIteration: 0,
            keyId: nextKeyId,
            signingPrivateKey: newSigningPriv,
            authorSigningPublicKey: IdentityKey.FromSpan(newSigningPub.Span));

        await senderKeyRepo.SaveSenderKeyRatchetAsync(newRatchet).ConfigureAwait(false);

        // Distribute rotated key ONLY to remaining members (excluding evicted member)
        var keyDist = scope.ServiceProvider.GetRequiredService<IGroupKeyDistributionService>();
        var targetMemberIds = remainingMembers
            .Where(m => m.IdentityId != IdentityId && m.IdentityId != memberToEvict.IdentityId)
            .Select(m => m.IdentityId)
            .ToList();

        var distResult = await keyDist.DistributeSenderKeyAsync(channelId, IdentityId, DeviceId.Primary, targetMemberIds).ConfigureAwait(false);
        if (distResult.IsFailure)
        {
            throw new InvalidOperationException($"Distribute rotated sender key failed: {distResult.Error!.Description}");
        }
    }

    public async Task<ChatMessageReceivedEvent> WaitForMessageAsync(ChannelId channelId, string expectedText, TimeSpan timeout)
    {
        var chatRepo = Services.GetRequiredService<IChatMessageRepository>();
        var existing = await chatRepo.GetByChannelIdAsync(channelId).ConfigureAwait(false);
        var match = existing.FirstOrDefault(m => m.Content == expectedText);
        if (match != null)
        {
            return new ChatMessageReceivedEvent(channelId, match.AuthorId, match.Content ?? string.Empty, DateTimeOffset.UtcNow);
        }

        var tcs = new TaskCompletionSource<ChatMessageReceivedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetException(new TimeoutException($"Timed out waiting for message '{expectedText}'.")));

        var subscriber = Services.GetRequiredService<IEventSubscriber>();
        using var sub = subscriber.Subscribe<ChatMessageReceivedEvent>((evt, _) =>
        {
            if (evt.ChannelId == channelId && evt.Plaintext == expectedText)
            {
                tcs.TrySetResult(evt);
            }
            return ValueTask.CompletedTask;
        });

        // Re-check after subscribing
        existing = await chatRepo.GetByChannelIdAsync(channelId).ConfigureAwait(false);
        match = existing.FirstOrDefault(m => m.Content == expectedText);
        if (match != null)
        {
            return new ChatMessageReceivedEvent(channelId, match.AuthorId, match.Content ?? string.Empty, DateTimeOffset.UtcNow);
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<InboundContactRequestReceivedEvent> WaitForInboundContactRequestAsync(TimeSpan timeout, PublicIdentityId? peerId = null)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        var tcs = new TaskCompletionSource<InboundContactRequestReceivedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        cts.Token.Register(() => tcs.TrySetException(new TimeoutException($"Timed out waiting for inbound contact request from {peerId?.Value}")));

        var subscriber = Services.GetRequiredService<IEventSubscriber>();
        using var sub = subscriber.Subscribe<InboundContactRequestReceivedEvent>((evt, _) =>
        {
            if (evt.OwnerIdentityId == IdentityId && (peerId == null || evt.RemotePeerId == peerId))
            {
                tcs.TrySetResult(evt);
            }
            return ValueTask.CompletedTask;
        });

        while (!tcs.Task.IsCompleted && !cts.IsCancellationRequested)
        {
            using var scope = Services.CreateScope();
            var contactRepo = scope.ServiceProvider.GetRequiredService<IPeerContactRepository>();
            var allContacts = await contactRepo.GetAllForOwnerAsync(IdentityId).ConfigureAwait(false);
            var pending = allContacts.FirstOrDefault(c => c.State == ContactState.PendingApproval && (peerId == null || c.RemotePeerId == peerId));
            if (pending != null)
            {
                return new InboundContactRequestReceivedEvent
                {
                    OwnerIdentityId = IdentityId,
                    RemotePeerId = pending.RemotePeerId,
                    PrimaryPublicKey = pending.PrimaryPublicKey!
                };
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<DirectChannelStatus> WaitForChannelStatusAsync(ChannelId channelId, DirectChannelStatus targetStatus, TimeSpan timeout)
    {
        var channelRepo = Services.GetRequiredService<IChannelRepository>();
        var current = await channelRepo.GetDirectByIdAsync(channelId).ConfigureAwait(false);
        if (current != null && current.Status == targetStatus)
        {
            return current.Status;
        }

        var tcs = new TaskCompletionSource<DirectChannelStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetCanceled());

        var subscriber = Services.GetRequiredService<IEventSubscriber>();
        using var sub1 = subscriber.Subscribe<DirectChannelActivatedEvent>((evt, _) =>
        {
            if (evt.ChannelId == channelId && targetStatus == DirectChannelStatus.Active)
            {
                tcs.TrySetResult(DirectChannelStatus.Active);
            }
            return ValueTask.CompletedTask;
        });
        using var sub2 = subscriber.Subscribe<DirectChannelCreatedEvent>((evt, _) =>
        {
            if (evt.ChannelId == channelId && evt.Status == targetStatus)
            {
                tcs.TrySetResult(evt.Status);
            }
            return ValueTask.CompletedTask;
        });

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (!tcs.Task.IsCompleted && !cts.IsCancellationRequested)
        {
            current = await channelRepo.GetDirectByIdAsync(channelId).ConfigureAwait(false);
            if (current != null && current.Status == targetStatus)
            {
                tcs.TrySetResult(current.Status);
                break;
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task StopDrainAsync()
    {
        _drainCts.Cancel();
        if (_drainLoopTask != null)
        {
            try
            {
                await _drainLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }
    }

    public async Task ResumeDrainAsync()
    {
        _drainCts = new CancellationTokenSource();
        var scheduler = Services.GetRequiredService<DrainScheduler>();
        _drainLoopTask = Task.Run(() => scheduler.RunAsync(_drainCts.Token));

        var publisher = Services.GetRequiredService<IEventPublisher>();
        await publisher.PublishAsync(new PersonaActivatedEvent(IdentityId)).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopDrainAsync().ConfigureAwait(false);

        foreach (var svc in _hostedServices)
        {
            try
            {
                await svc.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }

        await _serviceProvider.DisposeAsync().ConfigureAwait(false);
        await _keeperConnection.DisposeAsync().ConfigureAwait(false);
        _masterKeyHandle.Dispose();
    }

    private sealed class NotifyingChatMessageRepository : IChatMessageRepository
    {
        private readonly IChatMessageRepository _inner;
        private readonly IEventPublisher _eventPublisher;

        public NotifyingChatMessageRepository(IChatMessageRepository inner, IEventPublisher eventPublisher)
        {
            _inner = inner;
            _eventPublisher = eventPublisher;
        }

        public async Task AddAsync(ChatMessage record, CancellationToken ct = default)
        {
            await _inner.AddAsync(record, ct).ConfigureAwait(false);
            Console.WriteLine($"[CHAT_RECEIVED] Channel={record.ChannelId.Value}, Author={record.AuthorId.Value}, Content='{record.Content}'");
            await _eventPublisher.PublishAsync(
                new ChatMessageReceivedEvent(record.ChannelId, record.AuthorId, record.Content ?? string.Empty, DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        }

        public Task<ChatMessage?> GetByIdAsync(PayloadId id, CancellationToken ct = default) => _inner.GetByIdAsync(id, ct);
        public Task<IReadOnlyList<ChatMessage>> GetByChannelIdAsync(ChannelId channelId, CancellationToken ct = default) => _inner.GetByChannelIdAsync(channelId, ct);
        public Task UpdateAsync(ChatMessage record, CancellationToken ct = default) => _inner.UpdateAsync(record, ct);
        public Task<int> PurgeExpiredMessagesAsync(ulong currentTimestampUnixMs, CancellationToken ct = default) => _inner.PurgeExpiredMessagesAsync(currentTimestampUnixMs, ct);
    }
}
