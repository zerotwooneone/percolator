using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Identity;
using Percolator.Identity.Model;
using R3;

namespace Desktop.Wpf.Features.Self;

public sealed class IdentityStateService : IDisposable, IIdentityBootstrap, IIdentityStateService
{
    private readonly DisposableBag _bag;
    private readonly IIdentityScopeAccessor _identityScopeAccessor;
    private readonly PeerConnectionStateService _peerConnectionStateService;
    private readonly TimeProvider _timeProvider;

    private static readonly SelfIdentityModel None =
        new SelfIdentityModel(new SelfId(999), "Unknown", new ListeningPort(9999));
    private readonly ReactiveProperty<SelfIdentityModel> _activeIdentity = new(None);
    private readonly Subject<IdentityMutation> _mutationSubject = new();
    private readonly ILogger<IdentityStateService> _logger;
    public ReadOnlyReactiveProperty<SelfIdentityModel> ActiveIdentity => _activeIdentity;

    public IdentityStateService(
        IIdentityScopeAccessor identityScopeAccessor,
        PeerConnectionStateService peerConnectionStateService,
        ILogger<IdentityStateService> logger,
        TimeProvider timeProvider)
    {
        _identityScopeAccessor = identityScopeAccessor;
        _peerConnectionStateService = peerConnectionStateService;
        _logger = logger;
        _timeProvider = timeProvider;
        _bag = new DisposableBag();
        _activeIdentity.AddTo(ref _bag);
        _mutationSubject.AddTo(ref _bag);

        // Batch mutations with 250ms timeout
        _mutationSubject
            .Chunk(TimeSpan.FromMilliseconds(250), _timeProvider)
            .Where(mutations => mutations.Length > 0)
            .SubscribeAwait(async (mutations, ct) => await ProcessMutationsAsync(mutations, ct), AwaitOperation.Sequential)
            .AddTo(ref _bag);
    }

    public async Task BootstrapAsync(CancellationToken cancellationToken = default)
    {
        if (_identityScopeAccessor.Current is null)
        {
            throw new InvalidOperationException("Identity scope not available");
        }

        // Resolve the startup identity service to get the domain identity
        var startupIdentity = _identityScopeAccessor.Current.GetRequiredService<IStartupIdentityService>();
        var domainIdentity = await startupIdentity.ResolveOrCreateAsync(cancellationToken).ConfigureAwait(false);

        // Populate SelfIdentity model
        var displayName = domainIdentity.DisplayName?.Value ?? domainIdentity.Id.ToString();
        _activeIdentity.Value = new SelfIdentityModel(domainIdentity.Id,
            displayName,
            domainIdentity.ListeningPort,
            true);
        

        // Resolve application identity + keys and populate ActiveIdentityContext
        // This orchestrator is also responsible for publishing the ActiveIdentityLoadedEvent to boot infrastructure
        var orchestrator = _identityScopeAccessor.Current.GetRequiredService<IIdentityOrchestrator>();
        await orchestrator.ResolveIdentityAsync(domainIdentity.Id, cancellationToken).ConfigureAwait(false);

        // Initialize the peer connection state service with the self identity ID
        await _peerConnectionStateService.InitializeAsync(domainIdentity.Id, cancellationToken).ConfigureAwait(false);
    }

    public void UpdateDisplayName(SelfId targetId, string newName)
    {
        // Immediately update the UI model
        if (_activeIdentity.Value.Id == targetId)
        {
            var current = _activeIdentity.Value;
            _activeIdentity.Value = new SelfIdentityModel(targetId,
                newName,
                current.ListeningPort,
                current.Active);
        }

        // Push mutation to batch processor
        _mutationSubject.OnNext(new IdentityMutation(targetId, identity => identity.SetDisplayName(newName)));
    }

    private async Task ProcessMutationsAsync(IList<IdentityMutation> mutations, CancellationToken cancellationToken)
    {
        // Group by SelfId
        var grouped = mutations.GroupBy(m => m.TargetId);

        foreach (var group in grouped)
        {
            var targetId = group.Key;
            
            if (_identityScopeAccessor.Current is null)
            {
                _logger.LogError("Identity scope not available while processing mutations for {TargetId}", targetId);
                continue;
            }

            try
            {
                using var scope = _identityScopeAccessor.Current.CreateScope();
                var repository = scope.ServiceProvider.GetRequiredService<ISelfIdentityRepository>();
                var identity = await repository.GetByIdAsync(targetId, cancellationToken).ConfigureAwait(false);

                if (identity is null)
                {
                    _logger.LogError("Identity {TargetId} not found while processing mutations", targetId);
                    continue;
                }

                // Apply mutations in order
                foreach (var mutation in group)
                {
                    mutation.Apply(identity);
                }

                await repository.SaveAsync(identity, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process mutations for identity {TargetId}", targetId);
            }
        }
    }

    public void Dispose()
    {
        _bag.Dispose();
    }
}

public sealed record IdentityMutation(SelfId TargetId, Action<Percolator.Identity.Model.SelfIdentity> Apply);
