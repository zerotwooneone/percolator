using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Shell;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly ReactiveProperty<SelfIdentityModel> _activeIdentity = new(new SelfIdentityModel(new SelfId(999), "Not Initialized", "N I", new ListeningPort(9999)));
    public ReadOnlyReactiveProperty<SelfIdentityModel> ActiveIdentity => _activeIdentity;

    public IdentityStateService(
        IIdentityScopeAccessor identityScopeAccessor,
        PeerConnectionStateService peerConnectionStateService)
    {
        _identityScopeAccessor = identityScopeAccessor;
        _peerConnectionStateService = peerConnectionStateService;
        _bag = new DisposableBag();
        _activeIdentity.AddTo(ref _bag);
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
            ComputeInitials(displayName),
            domainIdentity.ListeningPort, 
            true);

        // Resolve application identity + keys and populate ActiveIdentityContext
        // This orchestrator is also responsible for publishing the ActiveIdentityLoadedEvent to boot infrastructure
        var orchestrator = _identityScopeAccessor.Current.GetRequiredService<IIdentityOrchestrator>();
        await orchestrator.ResolveIdentityAsync(domainIdentity.Id, cancellationToken).ConfigureAwait(false);

        // Initialize the peer connection state service with the self identity ID
        await _peerConnectionStateService.InitializeAsync(domainIdentity.Id, cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }

    public void Dispose()
    {
        _bag.Dispose();
    }
}
