namespace Percolator.Simulator.Features.Topology;

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Invitations;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Identities;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Shell;

internal sealed partial class PersonaInvitationExportFlyoutViewModel : ViewModelBase
{
    private readonly ISimulatedPersonaHost _host;
    private readonly IStatusToastService _toastService;

    [ObservableProperty]
    private string _generatedInvitationUrl = string.Empty;

    [ObservableProperty]
    private string? _selectedExchangeEndpoint;

    public ObservableCollection<string> AvailableExchanges { get; } = new();

    public PersonaInvitationExportFlyoutViewModel(
        ISimulatedPersonaHost host,
        IStatusToastService toastService)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        RefreshExchanges();
    }

    public void RefreshExchanges()
    {
        AvailableExchanges.Clear();
        var primaryRelay = _host.GetPrimaryLinkedRelay();
        if (primaryRelay != null)
        {
            AvailableExchanges.Add(primaryRelay.Endpoint.ToString());
            SelectedExchangeEndpoint = primaryRelay.Endpoint.ToString();
            GenerateInvitation();
        }
        else
        {
            GeneratedInvitationUrl = "No Exchange linked. Link an Exchange to share invitations.";
        }
    }

    partial void OnSelectedExchangeEndpointChanged(string? value)
    {
        GenerateInvitation();
    }

    [RelayCommand]
    public void GenerateInvitation()
    {
        var relay = _host.GetPrimaryLinkedRelay();
        if (relay == null)
        {
            GeneratedInvitationUrl = "No Exchange linked.";
            return;
        }

        using var scope = _host.Services.CreateScope();
        var invitationService = scope.ServiceProvider.GetRequiredService<IPeerInvitationService>();
        var result = invitationService.CreatePeerInvitationAsync(_host.PublicIdentityId, relay.Endpoint).GetAwaiter().GetResult();
        if (result.IsSuccess)
        {
            var payload = PeerInvitationPayload.FromDomain(result.Value);
            var codec = new PeerInvitationCodec();
            GeneratedInvitationUrl = codec.Encode(payload);
        }
        else
        {
            GeneratedInvitationUrl = $"Error: {result.Error.Description}";
        }
    }

    [RelayCommand]
    public async Task CopyInvitationUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(GeneratedInvitationUrl) || GeneratedInvitationUrl.StartsWith("No ") || GeneratedInvitationUrl.StartsWith("Error"))
        {
            return;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(GeneratedInvitationUrl);
        }

        _toastService.ShowToast("Copied", "Invitation URL copied to clipboard.", ToastSeverity.Success);
    }
}
