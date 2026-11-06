using System;
using System.IO;
using System.Linq;
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
using Percolator.Application2.Services;
using Percolator.Apps.Chat.DependencyInjection;
using Percolator.Apps.FileTransfer.Ports;
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
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Vault;
#if WINDOWS
using Percolator.Infrastructure.Windows.DependencyInjection;
using Percolator.Infrastructure.Windows.Security;
using Percolator.Infrastructure.Windows.Storage;
#endif
using Percolator.PluginSdk;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.Shell;
using R3;

namespace Percolator.Simulator.Features.Topology;

internal sealed class SimulatedPersonaHost : ISimulatedPersonaHost
{
    private readonly MasterKeyHandle _masterKeyHandle;
    private readonly ISimulatorLogSink _logSink;
    private readonly ISimulatorStateBus _stateBus;
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ReactiveProperty<bool> _isListening = new(true);
    private readonly string _nodeTempDirectory;
    private readonly EphemeralPrivateKey _identityPrivateKey;
    private readonly EphemeralPrivateKey _signedPreKeyPrivate;
    private IHost? _host;
    private KestrelPeerTransportServer? _transportServer;
    private bool _disposed;

    public SimulatedNodeId Id { get; }
    public string DisplayName { get; }
    public int BoundPort { get; private set; }
    public Uri BoundEndpoint => new($"https://127.0.0.1:{BoundPort}");
    public byte[] IdenticonPng { get; }
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started.");
    public ReadOnlyReactiveProperty<bool> IsListening => _isListening;

    public PublicIdentityId PublicIdentityId { get; }
    public IdentityKey IdentityPublicKey { get; }
    public DhPublicKey SignedPreKeyPublic { get; }
    public DeviceLinkProof SignedPreKeySignature { get; }

    public SimulatedPersonaHost(
        SimulatedNodeId id,
        string displayName,
        byte[] identiconPng,
        int port,
        ISimulatorLogSink logSink,
        ISimulatorStateBus stateBus,
        ISimulatedWorldStateService worldState)
    {
        Id = id;
        DisplayName = displayName;
        IdenticonPng = identiconPng;
        BoundPort = port;
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _masterKeyHandle = MasterKeyHandle.GenerateRandom();

        _nodeTempDirectory = Path.Combine(Path.GetTempPath(), "PercolatorSim", $"node_{Id.Value:N}");
        Directory.CreateDirectory(_nodeTempDirectory);

        // Procedural cryptographic identity initialization
        var crypto = new SignalCryptoEngine();
        var (ikPriv, ikPub) = crypto.GenerateEphemeralKeyPair();
        _identityPrivateKey = ikPriv;
        IdentityPublicKey = IdentityKey.FromSpan(ikPub.Span);
        PublicIdentityId = PublicIdentityId.New();

        var (spkPriv, spkPub) = crypto.GenerateEphemeralKeyPair();
        _signedPreKeyPrivate = spkPriv;
        SignedPreKeyPublic = spkPub;
        var sigBytes = crypto.SignEd25519(_identityPrivateKey.Span, SignedPreKeyPublic.Span);
        SignedPreKeySignature = DeviceLinkProof.FromSpan(sigBytes);
    }

    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        var storageOptions = new VaultStorageOptions
        {
            BaseDirectory = _nodeTempDirectory
        };

        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureServices((_, services) =>
        {
            // 1. Core infrastructure registration
            services.AddAuthenticatedInfrastructure(_masterKeyHandle, storageOptions);

            // 1a. Override DbConnectionInterceptor so SQLite operates in plain unencrypted mode (no sqlite3_key / SQLCipher encryption)
            services.AddSingleton<DbConnectionInterceptor, SimulatorSqlCipherPragmaInterceptor>();

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

            // 2. Chat vertical app registration
            services.AddChatApp();

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
            services.AddScoped<IPayloadSender, OutboundEgressPipeline>();
            services.AddScoped(sp => (SqliteLocalIdentityKeyStore)sp.GetRequiredService<ILocalIdentityKeyStore>());

            // 4a. Upsert decorator for IRatchetSessionRepository so re-handshaking / crossed handshakes replace the old SessionId row cleanly
            services.AddScoped<SqliteRatchetSessionRepository>();
            services.AddScoped<IRatchetSessionRepository>(sp =>
                new SimulatorRatchetSessionRepositoryDecorator(
                    sp.GetRequiredService<SqliteRatchetSessionRepository>(),
                    sp.GetRequiredService<PercolatorDbContext>()));

            // 5. Decorators & Ingress / Egress Pipelines
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

            services.AddScoped<HandshakeService>();
            services.AddScoped<IHandshakeService>(sp =>
            {
                var inner = sp.GetRequiredService<HandshakeService>();
                return new SimulatorNotifyingHandshakeDecorator(inner, _stateBus, Id);
            });

            services.AddScoped<IngressFilterService>();
            services.AddScoped<IInboundIngressPipeline>(sp =>
                new InboundIngressPipeline(
                    sp.GetRequiredService<IngressFilterService>(),
                    sp.GetRequiredService<IRatchetSessionRepository>(),
                    sp.GetRequiredService<IGroupReceiverSessionRepository>(),
                    sp.GetRequiredService<IHandshakeService>(),
                    sp.GetRequiredService<ICryptoEngine>(),
                    sp.GetRequiredService<IAppRouter>(),
                    sp.GetRequiredService<IChannelLockService>(),
                    sp.GetService<IGroupKeyDistributionService>()));

            services.AddScoped<ITransportDispatcher>(sp =>
            {
                var invoker = sp.GetRequiredService<ITransportClientInvoker>();
                return new SimulatorTransportDispatcher(invoker, _worldState, _logSink, DisplayName);
            });

            services.AddScoped<OutboxWorker>();

            // 6. Direct Ingress Bridge for Kestrel Transport
            services.AddSingleton<InboundEnvelopeContextHolder>();

            services.AddScoped<SealedEnvelopeUnwrapper>(sp =>
            {
                var wirePacker = sp.GetRequiredService<ISessionWirePacker>();
                var time = sp.GetRequiredService<IDateTimeProvider>();
                return new SealedEnvelopeUnwrapper(wirePacker, time, PublicIdentityId);
            });
            services.AddScoped<ISealedEnvelopeUnwrapper>(sp =>
            {
                var inner = sp.GetRequiredService<SealedEnvelopeUnwrapper>();
                var holder = sp.GetRequiredService<InboundEnvelopeContextHolder>();
                return new SimulatorDirectEnvelopeUnwrapper(inner, holder, _logSink, DisplayName);
            });

            services.AddScoped<IIngressFilterService>(sp =>
            {
                var inner = sp.GetRequiredService<IngressFilterService>();
                var holder = sp.GetRequiredService<InboundEnvelopeContextHolder>();
                var pipeline = sp.GetRequiredService<IInboundIngressPipeline>();
                return new SimulatorIngressDispatchingFilterService(inner, holder, pipeline, _logSink, DisplayName);
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

            var idKeyStore = (SqliteLocalIdentityKeyStore)scope.ServiceProvider.GetRequiredService<ILocalIdentityKeyStore>();
            await idKeyStore.SaveKeyAsync(PublicIdentityId, _identityPrivateKey.Span.ToArray(), IdentityPublicKey, ct);

            var preKeyStore = scope.ServiceProvider.GetRequiredService<IPrivatePreKeyStore>();
            await preKeyStore.StoreSignedPreKeyPrivateAsync(PublicIdentityId, DeviceId.Primary, _signedPreKeyPrivate, ct);
        }

        // 2. Bind Kestrel transport server on the allocated BoundPort before IHostedService startup
        _transportServer = _host.Services.GetRequiredService<KestrelPeerTransportServer>();
        if (!_transportServer.IsRunning)
        {
            await _transportServer.StartAsync(new PortNumber(BoundPort), ct);
        }

        if (_transportServer.BoundPort.HasValue)
        {
            BoundPort = _transportServer.BoundPort.Value.Value;
        }

        // 3. Start remaining background hosted services
        await _host.StartAsync(ct);

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            DisplayName,
            Id,
            "HostLifecycle",
            $"Persona host started and listening on https://127.0.0.1:{BoundPort} (Identity: {PublicIdentityId})"));
    }

    public PreKeyBundle CreatePreKeyBundle(uint? oneTimeKeyId = null)
    {
        return new PreKeyBundle(
            PublicIdentityId,
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

    public void SetListening(bool isListening)
    {
        _isListening.Value = isListening;
        UiTaskRunner.FireAndForget(
            async () =>
            {
                if (isListening)
                {
                    if (_transportServer is not null && !_transportServer.IsRunning)
                    {
                        await _transportServer.StartAsync(new PortNumber(BoundPort));
                        _logSink.Log(new SimulatorLogEntry(
                            DateTimeOffset.UtcNow,
                            LogLevel.Information,
                            DisplayName,
                            Id,
                            "HostLifecycle",
                            "Ingress resumed (Listening)."));
                    }
                }
                else
                {
                    if (_transportServer is not null && _transportServer.IsRunning)
                    {
                        await _transportServer.StopAsync();
                        _logSink.Log(new SimulatorLogEntry(
                            DateTimeOffset.UtcNow,
                            LogLevel.Information,
                            DisplayName,
                            Id,
                            "HostLifecycle",
                            "Ingress muted (Transport stopped)."));
                    }
                }
            },
            category: "HostLifecycle",
            logSink: _logSink,
            nodeName: DisplayName,
            nodeId: Id);
    }

    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (_transportServer is not null)
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
        _masterKeyHandle.Dispose();
        _isListening.Dispose();
        _identityPrivateKey.Dispose();
        _signedPreKeyPrivate.Dispose();

        try
        {
            if (Directory.Exists(_nodeTempDirectory))
            {
                Directory.Delete(_nodeTempDirectory, recursive: true);
            }
        }
        catch { }
    }
}
