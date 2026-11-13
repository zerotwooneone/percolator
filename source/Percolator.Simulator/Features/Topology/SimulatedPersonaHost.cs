using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2.Egress;
using Percolator.Application2.Handshake;
using Percolator.Application2.Ingress;
using Percolator.Application2.Ports;
using Percolator.Application2.Profiles;
using Percolator.Application2.Routing;
using Percolator.Application2.Services;
using Percolator.Apps.Chat.DependencyInjection;
using Percolator.Apps.FileTransfer.DependencyInjection;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.DependencyInjection;
using Percolator.Infrastructure.Persistence;
using Percolator.Infrastructure.Persistence.Repositories;
using Percolator.Infrastructure.Plugins.FileTransfer;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Vault;
#if WINDOWS
using Percolator.Infrastructure.Windows.DependencyInjection;
using Percolator.Infrastructure.Windows.Security;
using Percolator.Infrastructure.Windows.Storage;
#endif
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Shell;
using R3;
namespace Percolator.Simulator.Features.Topology;
internal sealed class SimulatedPersonaHost : ISimulatedPersonaHost
{
    private readonly MasterKeyHandle _masterKeyHandle;
    private readonly ISimulatorLogSink _logSink;
    private readonly ISimulatorStateBus _stateBus;
    private readonly ISimulatedWorldStateService _worldState;
    private readonly IVirtualClockStateService _virtualClock;
    private readonly ReactiveProperty<bool> _isListening = new(true);
    private readonly SemaphoreSlim _listeningGate = new(1, 1);
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly SemaphoreSlim _outboxGate = new(1, 1);
    private readonly string _nodeTempDirectory;
    private readonly EphemeralPrivateKey _identityPrivateKey;
    private readonly EphemeralPrivateKey _signedPreKeyPrivate;
    private IHost? _host;
    private KestrelPeerTransportServer? _transportServer;
    private IDisposable? _relayQueueSubscription;
    private bool _disposed;
    public SimulatedNodeId Id { get; }
    public string DisplayName { get; private set; }
    public int BoundPort { get; private set; }
    public Uri BoundEndpoint => new($"https://127.0.0.1:{BoundPort}");
    public byte[] IdenticonPng { get; private set; }
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started.");
    public ReadOnlyReactiveProperty<bool> IsListening => _isListening;
    public PublicIdentityId PublicIdentityId { get; }
    public IdentityKey IdentityPublicKey { get; }
    public DhPublicKey SignedPreKeyPublic { get; }
    public DeviceLinkProof SignedPreKeySignature { get; }
    public EphemeralPrivateKey IdentityPrivateKey => _identityPrivateKey;
    public BlindedRoutingToken MailboxRoutingToken { get; }
    public DeliveryToken MailboxDeliveryToken { get; private set; }
    public DeliveryToken MailboxDrainToken { get; }
    public ISimulatedRelayHost? HomeRelay { get; set; }
    public ProfileKey ProfileKey { get; private set; }
    public uint ProfileRevision { get; private set; } = 1;
    public bool AllowPeerDiscovery { get; set; } = true;
    public bool AutoShareProfile { get; set; } = true;
    public bool AutoAcceptGroupInvites { get; set; } = false;
    private bool _allowFileTransfers = false;
    public bool AllowFileTransfers
    {
        get => _allowFileTransfers;
        set
        {
            _allowFileTransfers = value;
            if (_host != null)
            {
                var filter = _host.Services.GetRequiredService<FileTransferIngressFilter>();
                filter.IsEnabled = value;
            }
        }
    }
    public bool RelayOnlyRouting { get; set; } = false;
    public bool AutoAcceptFileTransfers { get; set; } = false;
    public string NodeTempDirectory => _nodeTempDirectory;
    public string NodeDownloadsDirectory { get; }
    public SimulatedPersonaHost(
        SimulatedNodeId id,
        string displayName,
        byte[] identiconPng,
        int port,
        ISimulatorLogSink logSink,
        ISimulatorStateBus stateBus,
        ISimulatedWorldStateService worldState,
        IVirtualClockStateService virtualClock)
    {
        Id = id;
        DisplayName = displayName;
        IdenticonPng = identiconPng;
        BoundPort = port;
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
        _masterKeyHandle = MasterKeyHandle.GenerateRandom();
        _nodeTempDirectory = Path.Combine(Path.GetTempPath(), "PercolatorSim", $"node_{Id.Value:N}");
        Directory.CreateDirectory(_nodeTempDirectory);
        NodeDownloadsDirectory = Path.Combine(_nodeTempDirectory, "Downloads");
        Directory.CreateDirectory(NodeDownloadsDirectory);
        // Procedural cryptographic identity initialization
        var crypto = new SignalCryptoEngine(new CryptographyOptions());
        var (ikPriv, ikPub) = crypto.GenerateEphemeralKeyPair();
        _identityPrivateKey = ikPriv;
        IdentityPublicKey = IdentityKey.FromSpan(ikPub.Span);
        PublicIdentityId = PublicIdentityId.New();
        var (spkPriv, spkPub) = crypto.GenerateEphemeralKeyPair();
        _signedPreKeyPrivate = spkPriv;
        SignedPreKeyPublic = spkPub;
        var sigBytes = crypto.SignEd25519(_identityPrivateKey.Span, SignedPreKeyPublic.Span);
        SignedPreKeySignature = DeviceLinkProof.FromSpan(sigBytes);
        // ProfileKey
        ProfileKey = ProfileKey.Generate();
        // Relay tokens
        MailboxRoutingToken = BlindedRoutingToken.New();
        var drainBytes = new byte[32];
        RandomNumberGenerator.Fill(drainBytes);
        MailboxDrainToken = DeliveryToken.FromSpan(drainBytes);
        MailboxDeliveryToken = DeliveryToken.DeriveFromProfileKey(IdentityPublicKey, ProfileKey, crypto);
    }
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
#if !WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var errorMsg = "Persona host cannot start: Running on Windows under the generic 'net10.0' build target without Windows SChannel TLS infrastructure. " +
                           "Please build and launch using the 'net10.0-windows10.0.19041.0' target framework.";
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Critical,
                DisplayName,
                Id,
                "PlatformGuard",
                errorMsg));
            throw new PlatformNotSupportedException(errorMsg);
        }
#endif
        var storageOptions = new VaultStorageOptions
        {
            BaseDirectory = _nodeTempDirectory,
            UseEncryption = false
        };
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices((_, services) =>
        {
            // 1. Core infrastructure registration
            services.AddAuthenticatedInfrastructure(_masterKeyHandle, storageOptions);
            services.AddSingleton<INetworkEndpointDiscoveryPort>(_ => new NetworkInterfaceEndpointDiscoveryPort(BoundPort, allowLoopback: true));
            services.AddSingleton<IRelayMailboxCapabilityStore, SqliteRelayMailboxCapabilityStore>();
            services.AddSingleton<IDateTimeProvider>(_virtualClock);
#if WINDOWS
            // 1b. Windows-specific infrastructure overrides (SChannel TLS 1.3 cert provider & NTFS Sparse Desktop File Store)
            services.AddWindowsInfrastructureOverrides();
            services.AddSingleton<ITransportCertificateProvider>(
                _ => new WindowsSChannelCertificateProvider(Path.Combine(_nodeTempDirectory, "Certs")));
            services.AddSingleton<IDesktopFileStorage>(
                _ => new WindowsSparseDesktopFileStore(Path.Combine(_nodeTempDirectory, "Partials")));
#else
            // 1b. Linux / Cross-platform overrides (uses InMemoryTransportCertificateProvider for OpenSSL and node-scoped PortableDesktopFileStore)
            services.AddSingleton<IDesktopFileStorage>(
                _ => new PortableDesktopFileStore(Path.Combine(_nodeTempDirectory, "Partials")));
#endif
            // 2. Chat and FileTransfer vertical app registration
            services.AddChatApp();
            services.AddFileTransferApp();
            // 3. Node-scoped logger provider routing Percolator logs to multi-sink while suppressing EF Core / Kestrel connection spam
            services.AddLogging(lb =>
            {
                lb.ClearProviders();
                lb.SetMinimumLevel(LogLevel.Trace);
                lb.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
                lb.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
                lb.AddFilter("Microsoft.Extensions.Hosting", LogLevel.Information);
                lb.AddFilter("System.Net.Http", LogLevel.Warning);
                lb.AddProvider(new NodeScopedLoggerProvider(DisplayName, Id, _logSink));
            });
            // 4. Platform & Cross-Host dependencies
            services.AddSingleton<IChannelLockService, KeyedSemaphoreLockService>();
            services.AddSingleton<IHandshakeReplayFilter, MemoryHandshakeReplayFilter>();
            services.AddScoped<IContactRequestCoordinator, ContactRequestCoordinator>();
            services.AddScoped<IRoutingCoordinator, RoutingCoordinator>();
            services.AddScoped<IPayloadSender, OutboundEgressPipeline>();
            services.AddScoped<IGroupKeyDistributionService, GroupKeyDistributionService>();
            // 4b. Simulator-only read-model query services (isolated from core application/domain layers)
            services.AddScoped<ISimulatorChatQueryService, SimulatorChatQueryService>();
            services.AddScoped<ISimulatorRatchetQueryService, SimulatorRatchetQueryService>();
            // 5. Cross-Host Virtual Transport & Ingress Hooks
            // Decorate IAppRouter so chat messages notify ISimulatorStateBus (ChatTranscriptChanged)
            services.AddScoped<AppRouter>();
            services.AddScoped<IAppRouter>(sp =>
            {
                var inner = sp.GetRequiredService<AppRouter>();
                foreach (var handler in sp.GetServices<IAppPayloadHandler>())
                {
                    inner.RegisterHandler(handler);
                }
                foreach (var filter in sp.GetServices<IAppIngressFilter>())
                {
                    inner.RegisterFilter(filter);
                }
                return new SimulatorNotifyingAppRouterDecorator(inner, _stateBus, Id);
            });
            // Decorate IHandshakeService so incoming handshakes notify ISimulatorStateBus (PendingHandshakeChanged)
            services.AddScoped<HandshakeService>();
            services.AddScoped<IHandshakeService>(sp =>
                new SimulatorNotifyingHandshakeDecorator(
                    sp.GetRequiredService<HandshakeService>(),
                    _stateBus,
                    Id));
            services.AddScoped<OutboxWorker>();
            // 6. Direct Ingress for Kestrel Transport
            services.AddScoped<ISealedEnvelopeUnwrapper>(sp =>
            {
                var wirePacker = sp.GetRequiredService<ISessionWirePacker>();
                var time = sp.GetRequiredService<IDateTimeProvider>();
                return new SealedEnvelopeUnwrapper(wirePacker, PublicIdentityId, time);
            });
            services.AddScoped<IIngressFilterService, IngressFilterService>();
            services.AddScoped<IInboundIngressPipeline>(sp =>
            {
                var inner = new InboundIngressPipeline(
                    sp.GetRequiredService<IIngressFilterService>(),
                    sp.GetRequiredService<IRatchetSessionRepository>(),
                    sp.GetRequiredService<IGroupReceiverSessionRepository>(),
                    sp.GetRequiredService<IHandshakeService>(),
                    sp.GetRequiredService<ICryptoEngine>(),
                    sp.GetRequiredService<IAppRouter>(),
                    sp.GetRequiredService<IChannelLockService>(),
                    sp.GetRequiredService<IGroupKeyDistributionService>());
                var outboxWorker = sp.GetRequiredService<OutboxWorker>();
                return new SimulatorOutboxFlushingIngressPipelineDecorator(inner, outboxWorker, _logSink, DisplayName);
            });
        });
        _host = builder.Build();
        // 1. Create SQLite tables and seed identity/prekey stores BEFORE starting background hosted services
        using (var scope = _host.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<PercolatorDbContext>();
            try
            {
                var creator = dbContext.GetService<IRelationalDatabaseCreator>();
                await creator.CreateTablesAsync(ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
            {
                // Idempotent: tables already created in a previous session
            }
            var profileRepo = scope.ServiceProvider.GetRequiredService<IIdentityProfileRepository>();
            var timeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
            var profileResult = IdentityProfile.Create(PublicIdentityId, DisplayName, IdentityRole.UserPersona, timeProvider);
            if (profileResult.IsSuccess)
            {
                await profileRepo.SaveAsync(profileResult.Value, ct);
            }
            var idKeyStore = scope.ServiceProvider.GetRequiredService<ILocalIdentityKeyStore>();
            await idKeyStore.SaveIdentityKeysAsync(PublicIdentityId, _identityPrivateKey, IdentityPublicKey, ProfileKey, ct);
            var preKeyStore = scope.ServiceProvider.GetRequiredService<IPrivatePreKeyStore>();
            await preKeyStore.StoreSignedPreKeyAsync(PublicIdentityId, DeviceId.Primary, _signedPreKeyPrivate, SignedPreKeyPublic, SignedPreKeySignature, ct);
            var capStore = scope.ServiceProvider.GetRequiredService<IRelayMailboxCapabilityStore>();
            await capStore.RegisterVerifiedAsync(
                MailboxRoutingToken.ToByteArray(),
                MailboxDeliveryToken.Memory,
                MailboxDrainToken.Memory,
                ct: ct);
            var preKeyRepo = scope.ServiceProvider.GetRequiredService<IRelayPreKeyRepository>();
            var hostedBundle = RelayHostedPreKeyBundle.Rehydrate(
                MailboxRoutingToken,
                DeviceId.Primary,
                IdentityPublicKey,
                SignedPreKeyPublic,
                SignedPreKeySignature,
                oneTimePreKeys: null);
            await preKeyRepo.SaveAsync(hostedBundle, ct);
        }
        // 2. Bind Kestrel transport server on the allocated BoundPort before IHostedService startup
        _transportServer = _host.Services.GetRequiredService<KestrelPeerTransportServer>();
        if (!_transportServer.IsRunning)
        {
            await _transportServer.StartAsync(IPAddress.Loopback, new PortNumber(BoundPort), ct: ct);
        }
        if (_transportServer.BoundPort.HasValue)
        {
            BoundPort = _transportServer.BoundPort.Value.Value;
        }
        // 3. Start remaining background hosted services
        await _host.StartAsync(ct);
        var ftFilter = _host.Services.GetRequiredService<FileTransferIngressFilter>();
        ftFilter.IsEnabled = _allowFileTransfers;
        // 3a. Publish initial profile (revision 1) and avatar blind blob
        await PublishProfileAsync(ct);
        // 4. Reactive listener: auto-drain mailbox when relay queue changes
        _relayQueueSubscription = _stateBus.Signals
            .Where(s => s.Category == SignalCategory.RelayQueueChanged)
            .Subscribe(_ =>
            {
                if (_isListening.CurrentValue && HomeRelay != null && !_disposed)
                {
                    UiTaskRunner.FireAndForget(() => DrainMailboxAsync(), "MailboxDrain", _logSink);
                }
            });
        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            DisplayName,
            Id,
            "HostLifecycle",
            $"Persona host started and listening on https://127.0.0.1:{BoundPort} (Identity: {PublicIdentityId})"));
    }
    public void RotateProfileKey()
    {
        ProfileKey.Dispose();
        if (_host != null)
        {
            // Persist the rotation in the identity key store so GetMailboxTokensAsync and
            // DeliveryToken.DeriveFromProfileKey consumers observe the same key material.
            using var scope = _host.Services.CreateScope();
            ProfileKey = scope.ServiceProvider
                .GetRequiredService<ILocalIdentityKeyStore>()
                .RotateProfileKeyAsync(PublicIdentityId)
                .GetAwaiter().GetResult();
        }
        else
        {
            ProfileKey = ProfileKey.Generate();
        }
        MailboxDeliveryToken.Dispose();
        MailboxDeliveryToken = DeliveryToken.DeriveFromProfileKey(IdentityPublicKey, ProfileKey, new SignalCryptoEngine(new CryptographyOptions()));
    }
    public void UpdateProfile(string newDisplayName, byte[] newIdenticonPng)
    {
        DisplayName = newDisplayName ?? throw new ArgumentNullException(nameof(newDisplayName));
        IdenticonPng = newIdenticonPng ?? throw new ArgumentNullException(nameof(newIdenticonPng));
        ProfileRevision++;
        RotateProfileKey();
    }
    public async Task<DomainResult> PublishProfileAsync(CancellationToken ct = default)
    {
        if (_host is null)
        {
            return DomainResult.Failure(new DomainError("HOST_NOT_STARTED", "Persona host is not started."));
        }
        try
        {
            using var scope = _host.Services.CreateScope();
            var cryptoService = scope.ServiceProvider.GetRequiredService<IBlobCryptoService>();
            var blobClient = scope.ServiceProvider.GetRequiredService<IRelayBlobClient>();
            var profileManager = scope.ServiceProvider.GetRequiredService<IProfileManager>();
            var cryptoEngine = scope.ServiceProvider.GetRequiredService<ICryptoEngine>();
            var profileDeliveryClient = scope.ServiceProvider.GetRequiredService<IProfileDeliveryClient>();
            var timeProvider = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
            // 1. Encrypt persona's 64x64 Identicon PNG via IBlobCryptoService
            using var pngStream = new MemoryStream(IdenticonPng);
            var encAvatarResult = await cryptoService.EncryptAndPackageAsync(pngStream, "image/png", ct).ConfigureAwait(false);
            if (!encAvatarResult.IsSuccess)
            {
                return DomainResult.Failure(encAvatarResult.Error!);
            }
            var avatarPkg = encAvatarResult.Value;
            var avatarBlobId = BlobId.FromSha256(avatarPkg.CiphertextSha256);
            // Determine target upload endpoint
            var targetRelayUri = HomeRelay != null
                ? new Uri($"https://127.0.0.1:{HomeRelay.BoundPort}")
                : (_worldState.GetAllRelayHosts().FirstOrDefault() is { } r
                    ? new Uri($"https://127.0.0.1:{r.BoundPort}")
                    : BoundEndpoint);
            // 2. Upload avatar blind blob via IRelayBlobClient (UploadBlindBlob over gRPC)
            var uploadBlobResult = await blobClient.UploadBlobAsync(
                targetRelayUri,
                avatarBlobId,
                avatarPkg.CiphertextStream,
                default(ChannelId),
                ct: ct).ConfigureAwait(false);
            if (!uploadBlobResult.IsSuccess)
            {
                return DomainResult.Failure(uploadBlobResult.Error!);
            }
            var avatarRef = new BlobReference
            {
                Id = avatarBlobId,
                CiphertextSha256 = avatarPkg.CiphertextSha256,
                EncryptionKey = avatarPkg.EncryptionKey,
                BaseNonce = avatarPkg.BaseNonce,
                PlaintextSizeBytes = avatarPkg.PlaintextSize,
                CiphertextSizeBytes = avatarPkg.CiphertextSize,
                MimeType = "image/png",
                ExpiresAtUtc = timeProvider.UtcNow.AddDays(30),
                StorageMode = BlobStorageMode.PublicRelayBlob,
                RelayEndpointUri = targetRelayUri.ToString()
            };
            // 3. Encrypt ProfileMetadata into 1040-byte padded block via IProfileManager
            var metadata = new ProfileMetadata(
                DisplayName,
                IdenticonPng,
                $"Status bio for {DisplayName}",
                ProfileRevision,
                avatarRef);
            var encProfileResult = profileManager.EncryptProfile(metadata, ProfileKey, cryptoEngine);
            if (!encProfileResult.IsSuccess)
            {
                return DomainResult.Failure(encProfileResult.Error!);
            }
            var profilePkg = encProfileResult.Value;
            var locator = BlindedProfileLocator.Derive(IdentityPublicKey, ProfileKey, cryptoEngine);
            var writeToken = ProfileWriteToken.Derive(_identityPrivateKey, ProfileKey, cryptoEngine);
            // 4. Upload profile via IProfileDeliveryClient.UploadProfileAsync (UploadProfile over gRPC)
            var uploadProfileResult = await profileDeliveryClient.UploadProfileAsync(
                targetRelayUri,
                locator,
                writeToken,
                profilePkg,
                ProfileRevision,
                timeProvider.UtcNow.AddDays(30),
                ct).ConfigureAwait(false);
            if (!uploadProfileResult.IsSuccess)
            {
                return DomainResult.Failure(uploadProfileResult.Error!);
            }
            if (HomeRelay != null)
            {
                var provisioner = scope.ServiceProvider.GetRequiredService<IRelayMailboxProvisioner>();
                var relayEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", HomeRelay.BoundPort);
                var mailboxTokens = new MailboxTokenTriad(MailboxRoutingToken, MailboxDeliveryToken, MailboxDrainToken);
                var provisionResult = await provisioner.ProvisionMailboxAsync(
                    PublicIdentityId, HomeRelay.RelayIdentityId, relayEndpoint, mailboxTokens, ct).ConfigureAwait(false);
                if (provisionResult.IsFailure)
                {
                    return DomainResult.Failure(provisionResult.Error);
                }
            }
            // Also save locally in IRelayProfileRepository for direct P2P queries
            var localProfileRepo = scope.ServiceProvider.GetRequiredService<IRelayProfileRepository>();
            var localSlot = RelayHostedProfileSlot.Create(locator, writeToken, profilePkg, ProfileRevision, timeProvider.UtcNow.AddDays(30));
            if (localSlot.IsSuccess)
            {
                await localProfileRepo.SaveSlotAsync(localSlot.Value!, ct).ConfigureAwait(false);
            }
            // And update local identity profile
            var idProfileRepo = scope.ServiceProvider.GetRequiredService<IIdentityProfileRepository>();
            var identityProfile = IdentityProfile.Create(PublicIdentityId, DisplayName, IdentityRole.UserPersona, timeProvider);
            if (identityProfile.IsSuccess)
            {
                await idProfileRepo.SaveAsync(identityProfile.Value!, ct).ConfigureAwait(false);
            }
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Information,
                DisplayName,
                Id,
                "ProfileSync",
                $"Published profile revision {ProfileRevision} with locator {Convert.ToHexString(locator.Span)[..8]} and avatar blob {avatarBlobId} to {targetRelayUri}"));
            return DomainResult.Success();
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                DisplayName,
                Id,
                "ProfileSync",
                $"Failed to publish profile: {ex.Message}"));
            return DomainResult.Failure(new DomainError("PROFILE_PUBLISH_FAILED", ex.Message));
        }
    }
    public PreKeyBundle CreatePreKeyBundle(uint? oneTimeKeyId = null)
    {
        return new PreKeyBundle(
            DeviceId.Primary,
            IdentityPublicKey,
            SignedPreKeyPublic,
            SignedPreKeySignature,
            OneTimePreKey: null,
            OneTimePreKeyId: 0);
    }
    public async Task<DomainResult> FlushOutboxAsync(CancellationToken ct = default)
    {
        if (_host is null)
        {
            return DomainResult.Failure(new DomainError("HOST_NOT_STARTED", "Persona host is not started."));
        }
        await _outboxGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var scope = _host.Services.CreateScope();
        var outboxWorker = scope.ServiceProvider.GetRequiredService<OutboxWorker>();
        await outboxWorker.ProcessBatchAsync(50, ct);
        var outboxQuery = scope.ServiceProvider.GetRequiredService<IOutboxQueryService>();
        var ownerJobs = await outboxQuery.GetJobsForOwnerAsync(PublicIdentityId, ct);
        var failedOrRetrying = ownerJobs.FirstOrDefault(j =>
            j.Status == OutboxStatus.Failed ||
            (j.Status == OutboxStatus.Pending && j.RetryCount > 0));
        if (failedOrRetrying != null)
        {
            return DomainResult.Failure(new DomainError(
                "OUTBOX_DELIVERY_FAILED",
                $"Outbox delivery to peer failed (Status: {failedOrRetrying.Status}, Retries: {failedOrRetrying.RetryCount}). Check Log Dock for details."));
        }
            return DomainResult.Success();
        }
        finally
        {
            try { _outboxGate.Release(); } catch (ObjectDisposedException) { }
        }
    }
    public async Task<int> DrainMailboxAsync(CancellationToken ct = default)
    {
        if (!_isListening.CurrentValue || HomeRelay == null || _host == null || _disposed)
        {
            return 0;
        }
        try
        {
            await _drainGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
        try
        {
            if (!_isListening.CurrentValue || HomeRelay == null || _host == null || _disposed)
            {
                return 0;
            }
            using var scope = _host.Services.CreateScope();
            var provisioner = scope.ServiceProvider.GetRequiredService<IRelayMailboxProvisioner>();
            var relayEndpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", HomeRelay.BoundPort);
            var mailboxTokens = new MailboxTokenTriad(MailboxRoutingToken, MailboxDeliveryToken, MailboxDrainToken);
            var drainResult = await provisioner.DrainMailboxOnceAsync(PublicIdentityId, relayEndpoint, mailboxTokens, ct);
            if (drainResult.IsFailure)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    DisplayName,
                    Id,
                    "MailboxDrain",
                    $"Mailbox drain failed: {drainResult.Error.Code} - {drainResult.Error.Description}"));
                return 0;
            }
            int processed = drainResult.Value;
            if (processed > 0)
            {
                await _outboxGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var outboxWorker = scope.ServiceProvider.GetRequiredService<OutboxWorker>();
                    await outboxWorker.ProcessBatchAsync(50, ct);
                }
                finally
                {
                    try { _outboxGate.Release(); } catch (ObjectDisposedException) { }
                }
                _stateBus.Publish(new SimulatorStateSignal(
                    Id,
                    SignalCategory.ChatTranscriptChanged,
                    DisplayName));
                _stateBus.Publish(new SimulatorStateSignal(
                    Id,
                    SignalCategory.GroupStateChanged,
                    DisplayName));
            }
            return processed;
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            return 0;
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                DisplayName,
                Id,
                "MailboxDrain",
                $"Exception draining relay mailbox: {ex.Message}",
                ex.ToString()));
            return 0;
        }
        finally
        {
            try
            {
                _drainGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
    public async ValueTask<DomainResult> IngestGroupBroadcastAsync(byte[] framedPayload, CancellationToken ct = default)
    {
        if (!_isListening.CurrentValue || _host == null || _disposed)
        {
            return DomainResult.Failure(new DomainError("HOST_INACTIVE", "Persona host is not listening or disposed."));
        }
        using var scope = _host.Services.CreateScope();
        var unwrapper = scope.ServiceProvider.GetRequiredService<ISealedEnvelopeUnwrapper>();
        var ingress = scope.ServiceProvider.GetRequiredService<IInboundIngressPipeline>();
        var mailboxEnvelope = new MailboxEnvelope(
            EnvelopeId.New(),
            MailboxRoutingToken,
            framedPayload,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(7));
        var unwrapResult = unwrapper.Unwrap(mailboxEnvelope);
        if (unwrapResult.IsFailure)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Warning,
                DisplayName,
                Id,
                "GroupIngress",
                $"Group broadcast unwrap failed: {unwrapResult.Error.Code} - {unwrapResult.Error.Description}"));
            return DomainResult.Failure(unwrapResult.Error);
        }
        var ingressResult = await ingress.ProcessInboundAsync(unwrapResult.Value!, ct);
        if (ingressResult.IsFailure)
        {
            var logLevel = ingressResult.Error.Code == "EXPIRED_OR_DUPLICATE_MESSAGE"
                ? LogLevel.Debug
                : LogLevel.Warning;
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                logLevel,
                DisplayName,
                Id,
                "GroupIngress",
                $"Group broadcast ingress failed: {ingressResult.Error.Code} - {ingressResult.Error.Description}"));
            return DomainResult.Failure(ingressResult.Error);
        }
        await _outboxGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var outboxWorker = scope.ServiceProvider.GetRequiredService<OutboxWorker>();
            await outboxWorker.ProcessBatchAsync(50, ct);
        }
        finally
        {
            try { _outboxGate.Release(); } catch (ObjectDisposedException) { }
        }
        return DomainResult.Success();
    }
    public async Task SetListeningAsync(bool isListening, CancellationToken ct = default)
    {
        try
        {
            await _listeningGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            _isListening.Value = isListening;
            if (isListening)
            {
                if (_transportServer is not null && !_transportServer.IsRunning)
                {
                    await _transportServer.StartAsync(IPAddress.Loopback, new PortNumber(BoundPort), ct: ct).ConfigureAwait(false);
                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Information,
                        DisplayName,
                        Id,
                        "HostLifecycle",
                        "Ingress resumed (Listening)."));
                }
                if (HomeRelay != null)
                {
                    await DrainMailboxAsync(ct).ConfigureAwait(false);
                }
            }
            else
            {
                if (_transportServer is not null && _transportServer.IsRunning)
                {
                    await _transportServer.StopAsync(ct).ConfigureAwait(false);
                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Information,
                        DisplayName,
                        Id,
                        "HostLifecycle",
                        "Ingress muted (Transport stopped)."));
                }
            }
        }
        finally
        {
            try
            {
                _listeningGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        _relayQueueSubscription?.Dispose();
        _relayQueueSubscription = null;
        if (_transportServer is not null && _transportServer.IsRunning)
        {
            await _transportServer.StopAsync(ct);
        }
        if (_host is not null)
        {
            await _host.StopAsync(ct);
        }
        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            DisplayName,
            Id,
            "HostLifecycle",
            "Persona host stopped."));
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        _host?.Dispose();
        _listeningGate.Dispose();
        _drainGate.Dispose();
        _outboxGate.Dispose();
        _masterKeyHandle.Dispose();
        _isListening.Dispose();
        _identityPrivateKey.Dispose();
        _signedPreKeyPrivate.Dispose();
        ProfileKey.Dispose();
        MailboxDeliveryToken.Dispose();
        MailboxDrainToken.Dispose();
        try
        {
            if (Directory.Exists(_nodeTempDirectory))
            {
                Directory.Delete(_nodeTempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort temporary directory cleanup on node disposal
        }
    }
}
