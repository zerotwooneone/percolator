using Desktop.Wpf.Features.Sessions;
using Desktop.Wpf.Features.Shell;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Identity;
using Percolator.Identity;
using R3;

namespace Desktop.Wpf.Features.Self;

public sealed class IdentityStateService : IDisposable, IIdentityBootstrap, IIdentityStateService
{
    private readonly DisposableBag _bag;
    private readonly IIdentityScopeAccessor _identityScopeAccessor;
    private readonly PeerConnectionStateService _peerConnectionStateService;
    private readonly SelfIdentityModel _self;
    private readonly ReactiveProperty<string> _displayName;
    private readonly ReactiveProperty<bool> _active;

    public IdentityStateService(
        IIdentityScopeAccessor identityScopeAccessor,
        PeerConnectionStateService peerConnectionStateService,
        SelfIdentityModel self)
    {
        _identityScopeAccessor = identityScopeAccessor;
        _peerConnectionStateService = peerConnectionStateService;
        _self = self;
        _bag = new DisposableBag();
        _displayName = new ReactiveProperty<string>("").AddTo(ref _bag);
        _active = new ReactiveProperty<bool>(false).AddTo(ref _bag);
    }

    public SelfId? Id { get; private set; }
    public ReadOnlyReactiveProperty<string> DisplayName => _displayName;
    public ReadOnlyReactiveProperty<bool> Active => _active;

    public async Task BootstrapAsync(CancellationToken cancellationToken = default)
    {
        if (_identityScopeAccessor.Current is null)
        {
            throw new InvalidOperationException("Identity scope not available");
        }

        // Resolve the startup identity service to get the domain identity
        var startupIdentity = _identityScopeAccessor.Current.GetRequiredService<IStartupIdentityService>();
        var domainIdentity = await startupIdentity.ResolveOrCreateAsync().ConfigureAwait(false);

        // Populate SelfIdentity model
        var displayName = domainIdentity.DisplayName?.Value ?? domainIdentity.Id.ToString();
        _self.DisplayName.Value = displayName;
        _self.Initials.Value = ComputeInitials(displayName);
        _self.Id.Value = domainIdentity.Id.ToString();

        // Resolve application identity + keys and populate ActiveIdentityContext
        var orchestrator = _identityScopeAccessor.Current.GetRequiredService<IIdentityOrchestrator>();
        await orchestrator.ResolveIdentityAsync(domainIdentity.Id, cancellationToken).ConfigureAwait(false);

        // Initialize the peer connection state service with the self identity ID
        await _peerConnectionStateService.InitializeAsync(domainIdentity.Id, cancellationToken).ConfigureAwait(false);

        // Update the IdentityStateService state
        Id = domainIdentity.Id;
        _displayName.Value = displayName;
        _active.Value = true;
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
