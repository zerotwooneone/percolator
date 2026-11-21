// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.ViewModels;

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Windowing;
using Percolator.Desktop.Features.Chats.Models;
using Percolator.Desktop.Features.Chats.Services;
using Percolator.Desktop.ViewModels;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;

/// <summary>
/// ViewModel governing the modal Group Creation Dialog ([DESK-07], [VM-001]).
/// Restricts membership to peers sharing a common relay.
/// </summary>
public sealed partial class GroupCreationDialogViewModel : ViewModelBase
{
    private readonly IDialogContext<GroupCreationDialogParameter, GroupCreationDialogResult> _dialogContext;
    private readonly IPeerContactQueryService _contactQueryService;
    private readonly IPersonaRelayQueryService _relayQueryService;
    private readonly IConversationStateService _conversationStateService;
    private readonly ILogger<GroupCreationDialogViewModel> _logger;

    private readonly PublicIdentityId _activePersonaId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateGroupCommand))]
    private string _groupName = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateGroupCommand))]
    private PersonaRelayLinkReadModel? _selectedRelay;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    public ObservableCollection<PersonaRelayLinkReadModel> AvailableRelays { get; } = new();
    public ObservableCollection<ContactSelectionItemModel> Contacts { get; } = new();

    public bool CanCreate => !string.IsNullOrWhiteSpace(GroupName) &&
                             SelectedRelay != null &&
                             Contacts.Any(c => c.IsSelected);

    public GroupCreationDialogViewModel(
        IDialogContext<GroupCreationDialogParameter, GroupCreationDialogResult> dialogContext,
        IPeerContactQueryService contactQueryService,
        IPersonaRelayQueryService relayQueryService,
        IConversationStateService conversationStateService,
        ILogger<GroupCreationDialogViewModel> logger)
    {
        _dialogContext = dialogContext ?? throw new ArgumentNullException(nameof(dialogContext));
        _contactQueryService = contactQueryService ?? throw new ArgumentNullException(nameof(contactQueryService));
        _relayQueryService = relayQueryService ?? throw new ArgumentNullException(nameof(relayQueryService));
        _conversationStateService = conversationStateService ?? throw new ArgumentNullException(nameof(conversationStateService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _activePersonaId = dialogContext.Data.ActivePersonaId;
        _ = LoadAsync(dialogContext.Data);
    }

    public async Task LoadAsync(GroupCreationDialogParameter parameter, CancellationToken ct = default)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var relays = await _relayQueryService.GetRelayLinksForPersonaAsync(_activePersonaId, ct).ConfigureAwait(true);
            AvailableRelays.Clear();
            foreach (var r in relays)
            {
                AvailableRelays.Add(r);
            }

            if (parameter.InitialRelay != null)
            {
                SelectedRelay = AvailableRelays.FirstOrDefault(r => r.Endpoint.Equals(parameter.InitialRelay));
            }

            if (SelectedRelay == null && AvailableRelays.Count > 0)
            {
                SelectedRelay = AvailableRelays[0];
            }

            var contacts = await _contactQueryService.GetContactSummariesAsync(_activePersonaId, ct).ConfigureAwait(true);
            foreach (var existing in Contacts)
            {
                existing.PropertyChanged -= OnContactItemPropertyChanged;
            }
            Contacts.Clear();
            foreach (var c in contacts)
            {
                var item = new ContactSelectionItemModel(c.IdentityId, c.DisplayName);
                item.PropertyChanged += OnContactItemPropertyChanged;
                Contacts.Add(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load relays or contacts for group creation dialog");
            ErrorMessage = "Failed to load relays or contacts.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void CreateGroup()
    {
        ErrorMessage = null;
        if (!CanCreate || SelectedRelay == null) return;

        var channelId = ChannelId.New();
        var selectedMemberIds = Contacts.Where(c => c.IsSelected).Select(c => c.IdentityId).ToList();
        var groupName = GroupName.Trim();

        try
        {
            _conversationStateService.RegisterRelayedConversation(
                channelId,
                groupName,
                SelectedRelay.RelayIdentityId,
                isGroup: true);

            _dialogContext.Close(GroupCreationDialogResult.Success(
                channelId,
                groupName,
                SelectedRelay.Endpoint,
                selectedMemberIds));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete group creation in dialog");
            ErrorMessage = "Failed to create group.";
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _dialogContext.Close(GroupCreationDialogResult.Cancelled());
    }

    private void OnContactItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContactSelectionItemModel.IsSelected))
        {
            CreateGroupCommand.NotifyCanExecuteChanged();
        }
    }

    protected override void DisposeCore()
    {
        foreach (var item in Contacts)
        {
            item.PropertyChanged -= OnContactItemPropertyChanged;
        }
        Contacts.Clear();
        base.DisposeCore();
    }
}
