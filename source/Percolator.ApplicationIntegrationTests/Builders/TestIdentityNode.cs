using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Egress;
using Percolator.Application2.Handshake;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Application2.Profiles;
using Percolator.Application2.Routing;
using Percolator.Application2.Services;
using Percolator.Application2IntegrationTests.Adapters;
using Percolator.Application2IntegrationTests.Adapters.Queries;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Profiles;
using Percolator.Apps.Chat.Ingress;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure2.Serialization;
using Percolator.PluginSdk;

namespace Percolator.Application2IntegrationTests.Builders;

public sealed class TestIdentityNode
{
    public string Name { get; }
    public EndpointPort Port { get; }
    public DirectEndpointCandidate LocalEndpointCandidate { get; }
    public PublicIdentityId IdentityId { get; }
    public DeviceId DeviceId { get; } = DeviceId.Primary;

    public IdentityKey IdentityPublicKey { get; }
    public EphemeralPrivateKey IdentityPrivateKey { get; }

    public DhPublicKey SignedPreKeyPublic { get; }
    public EphemeralPrivateKey SignedPreKeyPrivate { get; }
    public DeviceLinkProof SignedPreKeySignature { get; }

    public BlindedRoutingToken MailboxRoutingToken { get; }
    public DeliveryToken MailboxDeliveryToken { get; }
    public DeliveryToken MailboxDrainToken { get; }

    public IDateTimeProvider TimeProvider { get; }
    public ICryptoEngine CryptoEngine { get; }

    // Profile Capabilities
    public ProfileKey ProfileKey { get; private set; }
    public IProfileManager ProfileManager { get; }
    public InMemoryPeerProfileRepository PeerProfileRepo { get; }
    public InMemoryPeerMailboxRoutingRepository PeerMailboxRoutingRepo { get; } = new();
    public IPeerProfileQueryService PeerProfileQueries { get; }
    public IProfileDeliveryClient ProfileDeliveryClient { get; }
    public InMemoryRelayProfileRepository LocalProfileRepository { get; } = new();

    // Media & Out-of-band Blob Storage
    public ChatMediaService MediaService { get; }
    public IBlobStorageService BlobStorage { get; }
    public InMemoryLocalChunkStore LocalChunkStore { get; } = new();
    public ScenarioBlobCryptoService BlobCryptoService { get; } = new();
    public LoopbackRelayBlobClient RelayBlobClient { get; }
    public LoopbackChannelRelayResolver ChannelRelayResolver { get; }

    // Driving Ports
    public IChatService ChatService { get; }
    public IHandshakeService HandshakeService { get; }
    public IPeerConnectionService PeerConnectionService { get; }
    public IPreKeyBundleProvider PreKeyBundleProvider { get; }
    public IContactRequestCoordinator ContactRequestCoordinator { get; }
    public GroupKeyDistributionService GroupKeyDistributionService { get; }
    public GroupInvitationService GroupInvitationService { get; }
    public OutboxWorker OutboxWorker { get; }
    public InboundIngressPipeline IngressPipeline { get; }
    public IStreamRegistry StreamRegistry { get; }
    public ITransportDispatcher TransportDispatcher { get; }
    public RoutingCoordinator RoutingCoordinator { get; }

    // CQRS Query Ports
    public IChatMessageQueryService ChatQueries { get; }
    public IConversationListQueryService ConversationQueries { get; }
    public IOutboxQueryService OutboxQueries { get; }
    public IPeerContactQueryService ContactQueries { get; }
    public IGroupInvitationQueryService GroupInvitationQueries { get; }

    // Encapsulated Stores & Adapters (Strict Encapsulation: Hidden from tests)
    private readonly InMemoryChatMessageRepository _chatRepo;
    private readonly InMemoryChannelRepository _channelRepo;
    private readonly InMemoryOutboxRepository _outboxRepo;
    private readonly InMemoryPeerContactRepository _contactRepo;
    private readonly InMemoryRatchetSessionRepository _sessionRepo;
    private readonly InMemoryPendingHandshakeRepository _pendingHandshakeRepo;
    private readonly InMemoryLocalIdentityKeyStore _identityKeyStore;
    private readonly InMemoryPrivatePreKeyStore _preKeyStore;
    private readonly InMemoryGroupSenderKeyRepository _groupSenderKeyRepo;
    private readonly InMemoryGroupReceiverSessionRepository _groupReceiverSessionRepo;
    private readonly InMemoryGroupInvitationRepository _groupInvitationRepo;
    private readonly InMemoryIngressFilterService _ingressFilterService;

    private readonly SealedEnvelopeUnwrapper _envelopeUnwrapper;
    private readonly InMemoryLoopbackNetworkSwitch _networkSwitch;

    public TestIdentityNode(
        string name,
        EndpointPort port,
        InMemoryLoopbackNetworkSwitch networkSwitch,
        ScenarioCryptoEngine? cryptoEngine = null,
        IDateTimeProvider? timeProvider = null)
    {
        Name = name;
        Port = port;
        _networkSwitch = networkSwitch ?? throw new ArgumentNullException(nameof(networkSwitch));
        CryptoEngine = cryptoEngine ?? new ScenarioCryptoEngine();
        TimeProvider = timeProvider ?? new TestDateTimeProvider();

        IdentityId = PublicIdentityId.New();
        LocalEndpointCandidate = new DirectEndpointCandidate(
            EndpointHost.Parse("localhost"),
            PortNumber.FromInt(Port.Value),
            DirectCandidateKind.DirectLan);

        // Generate Identity KeyPair
        var (ikPriv, ikPub) = CryptoEngine.GenerateEphemeralKeyPair();
        IdentityPrivateKey = ikPriv;
        IdentityPublicKey = IdentityKey.FromSpan(ikPub.Span);

        // Generate Signed PreKey & Sign with Identity Private Key
        var (spkPriv, spkPub) = CryptoEngine.GenerateEphemeralKeyPair();
        SignedPreKeyPrivate = spkPriv;
        SignedPreKeyPublic = spkPub;
        var sigBytes = CryptoEngine.SignEd25519(IdentityPrivateKey.Span, SignedPreKeyPublic.Span);
        SignedPreKeySignature = DeviceLinkProof.FromSpan(sigBytes);

        // Generate Blinded Routing Tokens
        MailboxRoutingToken = BlindedRoutingToken.New();

        var deliveryBytes = new byte[32];
        var drainBytes = new byte[32];
        Random.Shared.NextBytes(deliveryBytes);
        Random.Shared.NextBytes(drainBytes);
        MailboxDeliveryToken = DeliveryToken.FromSpan(deliveryBytes);
        MailboxDrainToken = DeliveryToken.FromSpan(drainBytes);

        // Initialize Profile Components
        ProfileKey = ProfileKey.Generate();
        ProfileManager = new ProfileManager(new ProtobufProfileSerializer());
        PeerProfileRepo = new InMemoryPeerProfileRepository();
        PeerProfileQueries = new PeerProfileQueryService(PeerProfileRepo);
        ProfileDeliveryClient = new LoopbackProfileDeliveryClient(_networkSwitch, TimeProvider);
        _networkSwitch.RegisterPeerProfileStore(IdentityId, LocalProfileRepository);

        // Register local chunk store on network switch
        _networkSwitch.RegisterChunkStore(IdentityId, LocalChunkStore);

        // Initialize Stores
        _chatRepo = new InMemoryChatMessageRepository();
        _channelRepo = new InMemoryChannelRepository();
        _outboxRepo = new InMemoryOutboxRepository();
        _contactRepo = new InMemoryPeerContactRepository();
        _sessionRepo = new InMemoryRatchetSessionRepository();
        _pendingHandshakeRepo = new InMemoryPendingHandshakeRepository();
        _identityKeyStore = new InMemoryLocalIdentityKeyStore();
        _preKeyStore = new InMemoryPrivatePreKeyStore();
        _groupSenderKeyRepo = new InMemoryGroupSenderKeyRepository();
        _groupReceiverSessionRepo = new InMemoryGroupReceiverSessionRepository();
        _groupInvitationRepo = new InMemoryGroupInvitationRepository();
        _ingressFilterService = new InMemoryIngressFilterService();

        // Populate local key stores
        _identityKeyStore.RegisterKey(IdentityId, IdentityPrivateKey.Span.ToArray(), IdentityPublicKey);
        _preKeyStore.StoreSignedPreKeyPrivateAsync(IdentityId, DeviceId, SignedPreKeyPrivate).GetAwaiter().GetResult();

        // Wire packing & unwrapping
        var wirePacker = new DefaultSessionWirePacker();
        _envelopeUnwrapper = new SealedEnvelopeUnwrapper(wirePacker, IdentityId, TimeProvider);

        // Networking adapters
        StreamRegistry = new LoopbackStreamRegistry(IdentityId, DeviceId, _networkSwitch, _channelRepo);
        TransportDispatcher = new LoopbackTransportDispatcher(_networkSwitch);
        RoutingCoordinator = new RoutingCoordinator(StreamRegistry, PeerMailboxRoutingRepo, TimeProvider);
        PreKeyBundleProvider = new LoopbackPreKeyBundleProvider(_networkSwitch);

        // Blob & Media Storage
        RelayBlobClient = new LoopbackRelayBlobClient(_networkSwitch);
        ChannelRelayResolver = new LoopbackChannelRelayResolver(_networkSwitch, PeerMailboxRoutingRepo);
        var peerStreamBlobClient = new LoopbackPeerStreamBlobClient(peerId =>
            _networkSwitch.TryGetChunkStore(peerId, out var cs) ? cs : null);

        BlobStorage = new BlobStorageService(
            _channelRepo,
            ChannelRelayResolver,
            StreamRegistry,
            BlobCryptoService,
            RelayBlobClient,
            peerStreamBlobClient,
            LocalChunkStore,
            TimeProvider);

        MediaService = new ChatMediaService(BlobStorage);

        // Egress & Locks
        var channelLockService = new KeyedSemaphoreLockService();
        var egressPipeline = new OutboundEgressPipeline(
            _sessionRepo,
            _groupSenderKeyRepo,
            wirePacker,
            CryptoEngine,
            _outboxRepo,
            StreamRegistry,
            TimeProvider,
            channelLockService);

        // Application Routing & Ingress
        var appRouter = new AppRouter();
        var serializer = new TestPayloadSerializer();
        var chatHandler = new ChatPayloadHandler(
            _chatRepo,
            _channelRepo,
            _groupInvitationRepo,
            serializer,
            TimeProvider,
            unknownGroupCache: null,
            blobStorage: BlobStorage,
            peerProfileRepo: PeerProfileRepo,
            profileDeliveryClient: ProfileDeliveryClient,
            profileManager: ProfileManager,
            cryptoEngine: CryptoEngine,
            peerMailboxRoutingRepo: PeerMailboxRoutingRepo);
        appRouter.RegisterHandler(chatHandler);

        // Driving Services
        ChatService = new ChatService(
            _chatRepo,
            _channelRepo,
            egressPipeline,
            serializer,
            TimeProvider,
            BlobStorage);

        ContactRequestCoordinator = new ContactRequestCoordinator(
            _contactRepo,
            TimeProvider);

        GroupKeyDistributionService = new GroupKeyDistributionService(
            _groupSenderKeyRepo,
            _groupReceiverSessionRepo,
            egressPipeline,
            CryptoEngine,
            RoutingCoordinator);

        GroupInvitationService = new GroupInvitationService(
            _groupInvitationRepo,
            _channelRepo,
            egressPipeline,
            serializer,
            TimeProvider);

        var replayFilter = new MemoryHandshakeReplayFilter();
        HandshakeService = new HandshakeService(
            _identityKeyStore,
            _preKeyStore,
            _contactRepo,
            ContactRequestCoordinator,
            _sessionRepo,
            _pendingHandshakeRepo,
            replayFilter,
            _outboxRepo,
            CryptoEngine,
            TimeProvider,
            appRouter,
            wirePacker,
            routingCoordinator: RoutingCoordinator);

        PeerConnectionService = new PeerConnectionService(
            IdentityId,
            DeviceId,
            PreKeyBundleProvider,
            HandshakeService,
            _channelRepo,
            TimeProvider);

        IngressPipeline = new InboundIngressPipeline(
            _ingressFilterService,
            _sessionRepo,
            _groupReceiverSessionRepo,
            HandshakeService,
            CryptoEngine,
            appRouter,
            channelLockService,
            GroupKeyDistributionService);

        OutboxWorker = new OutboxWorker(
            _outboxRepo,
            TransportDispatcher,
            TimeProvider);

        // CQRS Query Ports
        ChatQueries = new InMemoryChatMessageQueryService(_chatRepo);
        ConversationQueries = new InMemoryConversationListQueryService(_channelRepo, _chatRepo, _contactRepo);
        OutboxQueries = new InMemoryOutboxQueryService(_outboxRepo);
        ContactQueries = new InMemoryPeerContactQueryService(_contactRepo);
        GroupInvitationQueries = new InMemoryGroupInvitationQueryService(_groupInvitationRepo, IdentityId);

        // Register on network switch
        _networkSwitch.RegisterNode(Port, IdentityId, ReceiveWireBytesAsync);
    }

    public void AddPeerMailboxRoute(
        PublicIdentityId peerId,
        RelayEndpoint relayEndpoint,
        BlindedRoutingToken routingToken,
        DeliveryToken deliveryToken)
    {
        var capability = new PeerMailboxRoutingCapability(
            peerId,
            routingToken,
            deliveryToken,
            relayEndpoint,
            LastObservedAtUtc: TimeProvider.UtcNow);
        PeerMailboxRoutingRepo.SaveAsync(capability).GetAwaiter().GetResult();
    }

    public void RotateProfileKey()
    {
        ProfileKey.Dispose();
        ProfileKey = ProfileKey.Generate();
    }

    public DomainResult PublishProfileToRelay(
        RelayNode relay,
        string displayName,
        string? statusBio = null,
        uint revision = 1)
    {
        var metadata = new ProfileMetadata(displayName, null, statusBio, revision);
        var encResult = ProfileManager.EncryptProfile(metadata, ProfileKey, CryptoEngine);
        if (encResult.IsFailure) return DomainResult.Failure(encResult.Error);

        var locator = BlindedProfileLocator.Derive(IdentityPublicKey, ProfileKey, CryptoEngine);
        var writeToken = ProfileWriteToken.Derive(IdentityPrivateKey, ProfileKey, CryptoEngine);
        var expiresAtUtc = TimeProvider.UtcNow.AddDays(14);

        return relay.UploadProfile(locator, writeToken, encResult.Value, revision, expiresAtUtc);
    }

    public async Task<DomainResult> PublishProfileDirectAsync(
        string displayName,
        string? statusBio = null,
        uint revision = 1)
    {
        var metadata = new ProfileMetadata(displayName, null, statusBio, revision);
        var encResult = ProfileManager.EncryptProfile(metadata, ProfileKey, CryptoEngine);
        if (encResult.IsFailure) return DomainResult.Failure(encResult.Error);

        var locator = BlindedProfileLocator.Derive(IdentityPublicKey, ProfileKey, CryptoEngine);
        var writeToken = ProfileWriteToken.Derive(IdentityPrivateKey, ProfileKey, CryptoEngine);
        var expiresAtUtc = TimeProvider.UtcNow.AddDays(14);

        var slotResult = RelayHostedProfileSlot.Create(locator, writeToken, encResult.Value, revision, expiresAtUtc);
        if (slotResult.IsFailure) return DomainResult.Failure(slotResult.Error);

        await LocalProfileRepository.SaveSlotAsync(slotResult.Value!).ConfigureAwait(false);
        return DomainResult.Success();
    }

    public async Task<DomainResult> ShareProfileCapabilityAsync(
        ChannelId channelId,
        TestIdentityNode recipient,
        Percolator.PluginSdk.DeliveryRoute route,
        uint revision = 1)
    {
        var locator = BlindedProfileLocator.Derive(IdentityPublicKey, ProfileKey, CryptoEngine);
        var sourceEndpoint = route == Percolator.PluginSdk.DeliveryRoute.DirectP2P
            ? $"localhost:{Port.Value}"
            : "relay";
        var cmd = new ShareProfileCapabilityCommand(
            channelId,
            IdentityId,
            recipient.IdentityId,
            recipient.DeviceId,
            ProfileKey,
            locator,
            MailboxRoutingToken,
            Revision: revision,
            route,
            AuthorPublicKey: IdentityPublicKey,
            SourceEndpoint: sourceEndpoint);

        return await ChatService.ShareProfileCapabilityAsync(cmd).ConfigureAwait(false);
    }

    public InvitationLink CreateInvitationLink()
    {
        return new InvitationLink("localhost", Port.Value, Convert.ToHexString(IdentityPublicKey.Span));
    }

    public InvitationLink CreateRelayedInvitation(RelayNode relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        return InvitationLink.CreateRelayed(
            IdentityId,
            DeviceId,
            IdentityPublicKey,
            RelayEndpoint.FromHostAndPort("localhost", relay.Port.Value),
            MailboxRoutingToken,
            MailboxDeliveryToken);
    }

    public PreKeyBundle CreatePreKeyBundle(uint? oneTimeKeyId = null)
    {
        DhPublicKey? opk = null;
        uint opkId = 0;
        if (oneTimeKeyId.HasValue)
        {
            var (opkPriv, opkPub) = CryptoEngine.GenerateEphemeralKeyPair();
            opk = opkPub;
            opkId = oneTimeKeyId.Value;
            _preKeyStore.StoreOneTimePreKeysPrivateAsync(IdentityId, DeviceId, [(opkId, opkPriv)]).GetAwaiter().GetResult();
        }

        return new PreKeyBundle(
            IdentityId,
            DeviceId,
            IdentityPublicKey,
            SignedPreKeyPublic,
            SignedPreKeySignature,
            opk,
            opkId);
    }

    public void RegisterMailbox(RelayNode relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        relay.RegisterClientMailbox(IdentityId, MailboxRoutingToken, MailboxDeliveryToken, MailboxDrainToken);
        _networkSwitch.BindRecipientToRelay(IdentityId, relay);
    }

    public RelayHostedPreKeyBundle PublishHostedPreKeys(RelayNode relay, int oneTimeKeyCount = 5)
    {
        ArgumentNullException.ThrowIfNull(relay);

        var otpkList = new List<(uint KeyId, DhPublicKey Key)>();
        var otpkPrivList = new List<(uint KeyId, EphemeralPrivateKey Key)>();
        for (uint i = 1; i <= (uint)oneTimeKeyCount; i++)
        {
            var (priv, pub) = CryptoEngine.GenerateEphemeralKeyPair();
            otpkList.Add((i, pub));
            otpkPrivList.Add((i, priv));
        }

        _preKeyStore.StoreOneTimePreKeysPrivateAsync(IdentityId, DeviceId, otpkPrivList).GetAwaiter().GetResult();

        var bundle = RelayHostedPreKeyBundle.Create(
            IdentityId,
            DeviceId,
            IdentityPublicKey,
            SignedPreKeyPublic,
            SignedPreKeySignature,
            otpkList,
            relay.Policy,
            CryptoEngine).Value!;

        RegisterMailbox(relay);
        relay.PreKeyRepository.SaveAsync(bundle).GetAwaiter().GetResult();

        return bundle;
    }

    public async Task<int> DrainMailboxAsync(RelayNode relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        var drainResult = relay.MailboxQueue.TryDrainForToken(MailboxRoutingToken, MailboxDrainToken);
        if (!drainResult.IsSuccess)
        {
            return 0;
        }

        int processed = 0;
        foreach (var envelope in drainResult.Value!)
        {
            var unwrapResult = _envelopeUnwrapper.Unwrap(envelope);
            if (unwrapResult.IsSuccess)
            {
                var res = await IngressPipeline.ProcessInboundAsync(unwrapResult.Value!).ConfigureAwait(false);
                if (!res.IsSuccess)
                {
                    throw new InvalidOperationException($"IngressPipeline drain error on {Name}: {res.Error.Code} - {res.Error.Description}");
                }
                processed++;
            }
            else
            {
                throw new InvalidOperationException($"Unwrap drain error on {Name}: {unwrapResult.Error.Code} - {unwrapResult.Error.Description}");
            }
        }

        return processed;
    }

    public async Task AddApprovedContactAsync(TestIdentityNode peer)
    {
        var contact = new PeerContact(
            IdentityId,
            peer.IdentityId,
            ContactNickname.Create(peer.Name, peer.IdentityId),
            PeerTrustLevel.Verified,
            TimeProvider.UtcNow,
            peer.IdentityPublicKey,
            ContactState.Active);

        await _contactRepo.SaveAsync(contact).ConfigureAwait(false);
    }

    public async Task<ChannelId> EstablishDirectChannelWithAsync(TestIdentityNode peer)
    {
        var channelId = ChannelId.New();
        var myChannel = DirectChannel.Create(channelId, IdentityId, peer.IdentityId, TimeProvider).Value!;
        var peerChannel = DirectChannel.Create(channelId, peer.IdentityId, IdentityId, TimeProvider).Value!;

        await _channelRepo.SaveDirectAsync(myChannel).ConfigureAwait(false);
        await peer.SaveDirectChannelInternalAsync(peerChannel).ConfigureAwait(false);
        return channelId;
    }

    public async Task SaveGroupChannelInternalAsync(GroupChannel channel)
    {
        await _channelRepo.SaveGroupAsync(channel).ConfigureAwait(false);
    }

    public async Task ReceiveWireBytesAsync(ReadOnlyMemory<byte> frameBytes)
    {
        var unwrapResult = _envelopeUnwrapper.UnwrapFrame(frameBytes, IdentityId, TimeProvider.UtcNow);
        if (unwrapResult.IsSuccess)
        {
            var res = await IngressPipeline.ProcessInboundAsync(unwrapResult.Value!).ConfigureAwait(false);
            if (!res.IsSuccess)
            {
                throw new InvalidOperationException($"IngressPipeline error on {Name}: {res.Error.Code} - {res.Error.Description}");
            }
        }
        else
        {
            throw new InvalidOperationException($"UnwrapFrame error on {Name}: {unwrapResult.Error.Code} - {unwrapResult.Error.Description}");
        }
    }

    /// <summary>
    /// Helper to bootstrap an active 1:1 Double Ratchet session, shared DirectChannel,
    /// mutual verified contact, and direct loopback stream connection between this node and a peer.
    /// </summary>
    public async Task<ChannelId> EstablishDirectSessionWithAsync(TestIdentityNode peer)
    {
        var channelId = ChannelId.New();

        // 1. Mutual Contacts
        var myContactOfPeer = new PeerContact(
            IdentityId,
            peer.IdentityId,
            ContactNickname.Create(peer.Name, peer.IdentityId),
            PeerTrustLevel.Verified,
            TimeProvider.UtcNow,
            peer.IdentityPublicKey,
            ContactState.Active);

        var peerContactOfMe = new PeerContact(
            peer.IdentityId,
            IdentityId,
            ContactNickname.Create(Name, IdentityId),
            PeerTrustLevel.Verified,
            TimeProvider.UtcNow,
            IdentityPublicKey,
            ContactState.Active);

        await _contactRepo.SaveAsync(myContactOfPeer).ConfigureAwait(false);
        await peer.SaveDirectContactInternalAsync(peerContactOfMe).ConfigureAwait(false);

        // 2. Mutual Direct Channels
        var myChannel = DirectChannel.Create(channelId, IdentityId, peer.IdentityId, TimeProvider).Value!;
        var peerChannel = DirectChannel.Create(channelId, peer.IdentityId, IdentityId, TimeProvider).Value!;
        await _channelRepo.SaveDirectAsync(myChannel).ConfigureAwait(false);
        await peer.SaveDirectChannelInternalAsync(peerChannel).ConfigureAwait(false);

        // 3. Mutual X3DH / Double Ratchet Session
        var peerBundle = peer.CreatePreKeyBundle();
        var initiatorResult = X3dhAgreement.Initiate(
            IdentityPrivateKey.Span,
            IdentityPublicKey,
            peerBundle,
            CryptoEngine).Value!;

        var initiatorSession = DirectRatchetSession.CreateFromX3dhInitiator(
            IdentityId,
            DeviceId,
            peer.IdentityId,
            peer.DeviceId,
            initiatorResult,
            peerBundle.SignedPreKey,
            CryptoEngine).Value!;

        var responderMasterSecret = X3dhAgreement.Receive(
            peer.IdentityPrivateKey.Span,
            peer.SignedPreKeyPrivate.Span,
            ReadOnlySpan<byte>.Empty,
            IdentityPublicKey,
            initiatorResult.EphemeralPublicKey,
            CryptoEngine).Value!;

        var responderSession = DirectRatchetSession.CreateFromX3dhResponder(
            peer.IdentityId,
            peer.DeviceId,
            IdentityId,
            DeviceId,
            responderMasterSecret,
            initiatorResult.EphemeralPublicKey,
            CryptoEngine).Value!;

        await _sessionRepo.SaveSessionAsync(initiatorSession).ConfigureAwait(false);
        await peer.SaveRatchetSessionInternalAsync(responderSession).ConfigureAwait(false);

        // 4. Open Direct Stream on Loopback Switch
        _networkSwitch.OpenDirectStream(Port, peer.Port);

        return channelId;
    }

    /// <summary>
    /// Establishes an active 1:1 Double Ratchet session, shared DirectChannel, and mutual verified contacts
    /// between this node (initiator) and a responder over a zero-knowledge relay using a realistic offline
    /// prekey bundle publication and Base64 invitation flow.
    /// </summary>
    public async Task<ChannelId> EstablishRelayedSessionWithAsync(
        TestIdentityNode responder,
        RelayNode relay)
    {
        ArgumentNullException.ThrowIfNull(responder);
        ArgumentNullException.ThrowIfNull(relay);

        // 1. Responder registers mailbox & publishes prekeys to Relay
        responder.PublishHostedPreKeys(relay, oneTimeKeyCount: 5);
        RegisterMailbox(relay);

        // 2. Responder creates Base64 invitation out-of-band
        var invite = responder.CreateRelayedInvitation(relay);
        var inviteBase64 = invite.ToBase64();

        // 3. Initiator receives Base64 invitation out-of-band and parses it
        var parsedInvite = InvitationLink.Parse(inviteBase64);

        // 4. Initiator connects via PeerConnectionService (fetches prekey bundle, initiates handshake, provisions channel)
        var connectResult = await PeerConnectionService.ConnectToPeerAsync(
            parsedInvite.PeerIdentityId!.Value,
            parsedInvite.PeerDeviceId,
            parsedInvite.RelayEndpoint).ConfigureAwait(false);

        // 5. Register mutual mailbox routing paths
        AddPeerMailboxRoute(parsedInvite.PeerIdentityId.Value, parsedInvite.RelayEndpoint!, parsedInvite.RoutingToken!.Value, parsedInvite.DeliveryToken!);
        responder.AddPeerMailboxRoute(IdentityId, parsedInvite.RelayEndpoint!, MailboxRoutingToken, MailboxDeliveryToken);

        if (!connectResult.IsSuccess)
        {
            throw new InvalidOperationException($"ConnectToPeer failed: {connectResult.Error.Code} - {connectResult.Error.Description}");
        }

        // 7. Initiator outbox worker flushes envelope to Relay mailbox
        await OutboxWorker.ProcessBatchAsync().ConfigureAwait(false);

        // 8. Responder comes online, drains mailbox, and completes handshake
        await responder.DrainMailboxAsync(relay).ConfigureAwait(false);
        await responder.ContactRequestCoordinator.ApproveRequestAsync(
            responder.IdentityId, IdentityId, PeerTrustLevel.Verified).ConfigureAwait(false);
        await responder.HandshakeService.CompletePendingHandshakeAsync(
            responder.IdentityId, IdentityId).ConfigureAwait(false);

        // 9. Establish mutual approved contacts and channels
        await AddApprovedContactAsync(responder).ConfigureAwait(false);
        await responder.AddApprovedContactAsync(this).ConfigureAwait(false);
        return await EstablishDirectChannelWithAsync(responder).ConfigureAwait(false);
    }

    internal Task SaveDirectContactInternalAsync(PeerContact contact) => _contactRepo.SaveAsync(contact);
    internal Task SaveDirectChannelInternalAsync(DirectChannel channel) => _channelRepo.SaveDirectAsync(channel);
    internal Task SaveRatchetSessionInternalAsync(DirectRatchetSession session) => _sessionRepo.SaveSessionAsync(session);
}
