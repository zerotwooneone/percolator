using Desktop.Wpf.Shared.Mvvm;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using R3;

namespace Desktop.Wpf.Features.Self;

public sealed class SelfIdentitySettingsViewModel : ViewModelBase
{
    private readonly IMediator _mediator;
    private readonly ILogger<SelfIdentitySettingsViewModel> _logger;
    private readonly IIdentityStateService _identityStateService;
    private readonly DisposableBag _bag;

    public BindableReactiveProperty<string> DisplayName { get; }
    public BindableReactiveProperty<string> ListeningPort { get; }
    public ReactiveCommand SaveCommand { get; }
    public ReactiveCommand CancelCommand { get; }

    public SelfIdentitySettingsViewModel(
        IMediator mediator,
        ILogger<SelfIdentitySettingsViewModel> logger,
        IIdentityStateService identityStateService)
    {
        _mediator = mediator;
        _logger = logger;
        _identityStateService = identityStateService;
        _bag = new DisposableBag();

        var self = _identityStateService.ActiveIdentity.CurrentValue;
        
        // Initialize with current display name from SelfIdentityModel
        DisplayName = self.DisplayName.ToBindableReactiveProperty().AddTo(ref _bag);
        
        // Listening port is read-only from ActiveIdentityContext (accessed via state service or self model)
        // For now, we'll show it as N/A since it's not directly exposed on SelfIdentityModel
        ListeningPort = self.ListeningPort.Select(l=>l.ToString()).ToBindableReactiveProperty().AddTo(ref _bag);

        SaveCommand = new ReactiveCommand().AddTo(ref _bag);
        SaveCommand.SubscribeAwait(async (_,_)=>await SaveAsync(), AwaitOperation.Drop);

        CancelCommand = new ReactiveCommand().AddTo(ref _bag);
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(DisplayName.Value))
        {
            _logger.LogWarning("Display name cannot be empty");
            return;
        }

        if (_identityStateService.ActiveIdentity.CurrentValue is null)
        {
            _logger.LogError("No active identity");
            return;
        }

        try
        {
            await _mediator.Send(new UpdateSelfDisplayNameCommand(
                _identityStateService.ActiveIdentity.CurrentValue.Id,
                DisplayName.Value)).ConfigureAwait(false);
            
            // Close the window by setting a result or relying on the window's behavior
            // The window will handle closing itself
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update display name");
        }
    }

    protected override void DisposeCore()
    {
        _bag.Dispose();
    }
}
