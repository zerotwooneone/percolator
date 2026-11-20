// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Common.Navigation;

using System;
using Microsoft.Extensions.Logging;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities;
using R3;

/// <summary>
/// Free-threaded UI state service managing navigation targets across the Left Rail,
/// Right Sidebar, and Inspector Drawer ([UISS-001], [UISS-002]).
/// </summary>
public sealed class ShellNavigationStateService : IShellNavigationStateService
{
    private readonly ILogger<ShellNavigationStateService> _logger;
    private readonly object _gate = new();

    private readonly ReactiveProperty<LeftRailTarget> _activeLeftRailTarget = new(LeftRailTarget.DirectMessages);
    private readonly ReactiveProperty<ChannelId?> _selectedConversationId = new(null);
    private readonly ReactiveProperty<bool> _isInspectorOpen = new(false);
    private readonly ReactiveProperty<InspectorTarget?> _activeInspectorTarget = new(null);

    private bool _disposed;

    public ShellNavigationStateService(ILogger<ShellNavigationStateService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ReadOnlyReactiveProperty<LeftRailTarget> ActiveLeftRailTarget => _activeLeftRailTarget;
    public ReadOnlyReactiveProperty<ChannelId?> SelectedConversationId => _selectedConversationId;
    public ReadOnlyReactiveProperty<bool> IsInspectorOpen => _isInspectorOpen;
    public ReadOnlyReactiveProperty<InspectorTarget?> ActiveInspectorTarget => _activeInspectorTarget;

    public void NavigateLeftRail(LeftRailTarget target)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(target);

        bool changed = false;
        lock (_gate)
        {
            if (!_activeLeftRailTarget.Value.Equals(target))
            {
                _logger.LogInformation("Navigating Left Rail to {Target}", target);
                changed = true;
            }
        }

        if (changed)
        {
            _activeLeftRailTarget.Value = target;
        }
    }

    public void NavigateToDirectMessages()
    {
        NavigateLeftRail(LeftRailTarget.DirectMessages);
    }

    public void NavigateToRelay(PublicIdentityId relayId)
    {
        if (!relayId.IsValid) return;
        NavigateLeftRail(LeftRailTarget.Relay(relayId));
    }

    public void SelectConversation(ChannelId? conversationId)
    {
        if (_disposed) return;

        bool changed = false;
        lock (_gate)
        {
            if (_selectedConversationId.Value != conversationId)
            {
                _logger.LogInformation("Selected conversation changed to {ConversationId}", conversationId?.ToString() ?? "None");
                changed = true;
            }
        }

        if (changed)
        {
            _selectedConversationId.Value = conversationId;
        }
    }

    public void OpenInspector(InspectorTarget target)
    {
        if (_disposed) return;
        ArgumentNullException.ThrowIfNull(target);

        lock (_gate)
        {
            _logger.LogInformation("Opening Inspector for {Kind}:{TargetId}", target.Kind, target.TargetId);
        }

        _activeInspectorTarget.Value = target;
        _isInspectorOpen.Value = true;
    }

    public void CloseInspector()
    {
        if (_disposed) return;

        lock (_gate)
        {
            _logger.LogInformation("Closing Inspector drawer");
        }

        _isInspectorOpen.Value = false;
    }

    public void ToggleInspector()
    {
        if (_disposed) return;

        bool nextState;
        lock (_gate)
        {
            nextState = !_isInspectorOpen.Value;
        }

        _isInspectorOpen.Value = nextState;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _activeLeftRailTarget.Dispose();
        _selectedConversationId.Dispose();
        _isInspectorOpen.Dispose();
        _activeInspectorTarget.Dispose();
    }
}
