using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ObservableCollections;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.Scenarios;
using Percolator.Simulator.Features.Scenarios.Model;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;
using R3;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Simulator.Features.GroupMessaging;

internal sealed record GroupMemberItemViewModel(
    PublicIdentityId IdentityId,
    string DisplayName,
    ChannelRole Role,
    bool CanEvict)
{
    public string RoleBadge => Role == ChannelRole.Admin ? "👑 Admin" : "👤 Member";
}

internal sealed partial class GroupPanelViewModel : ViewModelBase
{
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorStateBus _stateBus;
    private readonly ISimulatedWorldStateService _worldState;
    private readonly GroupChatCoordinator _groupCoordinator;
    private readonly ISimulatorLogSink _logSink;
    private readonly IFlightRecorderStateService _flightRecorder;

    public SimulatedNodeId NodeId { get; }
    public ChannelId ChannelId { get; }

    [ObservableProperty]
    private string _groupName;

    [ObservableProperty]
    private ulong _currentEpoch = 1;

    [ObservableProperty]
    private string _relayEndpoint = string.Empty;

    [ObservableProperty]
    private string _newGroupName = string.Empty;

    [ObservableProperty]
    private ISimulatedPersonaHost? _selectedCandidate;

    public ObservableCollection<GroupMemberItemViewModel> Members { get; } = new();
    public ObservableCollection<ISimulatedPersonaHost> Candidates { get; } = new();

    public GroupPanelViewModel(
        IStatusToastService toastService,
        ISimulatorStateBus stateBus,
        ISimulatedWorldStateService worldState,
        GroupChatCoordinator groupCoordinator,
        ISimulatorLogSink logSink,
        IFlightRecorderStateService flightRecorder)
        : this(
            new SimulatedNodeSummary(SimulatedNodeId.CreateNew(), "Default Group", SimulatedNodeType.Group, "Relay: None"),
            toastService,
            stateBus,
            worldState,
            groupCoordinator,
            logSink,
            flightRecorder)
    {
    }

    public GroupPanelViewModel(
        SimulatedNodeSummary summary,
        IStatusToastService toastService,
        ISimulatorStateBus stateBus,
        ISimulatedWorldStateService worldState,
        GroupChatCoordinator groupCoordinator,
        ISimulatorLogSink logSink,
        IFlightRecorderStateService flightRecorder)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(toastService);
        ArgumentNullException.ThrowIfNull(stateBus);
        ArgumentNullException.ThrowIfNull(worldState);
        ArgumentNullException.ThrowIfNull(groupCoordinator);
        ArgumentNullException.ThrowIfNull(logSink);
        ArgumentNullException.ThrowIfNull(flightRecorder);

        NodeId = summary.Id;
        ChannelId = new ChannelId(summary.Id.Value);
        _groupName = summary.DisplayName;
        _relayEndpoint = summary.Subtitle;
        _toastService = toastService;
        _stateBus = stateBus;
        _worldState = worldState;
        _groupCoordinator = groupCoordinator;
        _logSink = logSink;
        _flightRecorder = flightRecorder;

        _stateBus.Signals
            .Where(s => s.Category == SignalCategory.GroupStateChanged)
            .Subscribe(s =>
            {
                if (s.TargetChannelId == null || s.TargetChannelId == ChannelId)
                {
                    RefreshGroupDetails();
                }
            })
            .AddTo(ref Disposables);

        _worldState.Personas.ObserveChanged()
            .Subscribe(_ => RefreshGroupDetails())
            .AddTo(ref Disposables);

        RefreshGroupDetails();
    }

    public void RefreshGroupDetails()
    {
        UiTaskRunner.FireAndForget(async () =>
        {
            var (adminHost, _) = GetAdminAndRelay();
            var details = await _groupCoordinator.GetGroupDetailsAsync(ChannelId, adminHost);
            if (details == null) return;

            UiTaskRunner.PostToUi(() =>
            {
                GroupName = details.Value.Name;
                CurrentEpoch = details.Value.CurrentEpoch;

                var memberVms = details.Value.Members.Select(m => new GroupMemberItemViewModel(
                    m.IdentityId,
                    m.DisplayName,
                    m.Role,
                    CanEvict: m.Role != ChannelRole.Admin)).ToList();

                if (!Members.SequenceEqual(memberVms))
                {
                    Members.Clear();
                    foreach (var mem in memberVms)
                    {
                        Members.Add(mem);
                    }
                }

                var existingMemberIds = details.Value.Members.Select(m => m.IdentityId).ToHashSet();
                var candidateHosts = _worldState.GetAllPersonaHosts()
                    .Where(h => !existingMemberIds.Contains(h.PublicIdentityId))
                    .ToList();

                if (!Candidates.SequenceEqual(candidateHosts))
                {
                    Candidates.Clear();
                    foreach (var cand in candidateHosts)
                    {
                        Candidates.Add(cand);
                    }
                }

                if (SelectedCandidate == null || !Candidates.Contains(SelectedCandidate))
                {
                    SelectedCandidate = Candidates.FirstOrDefault();
                }
            });
        },
        category: "GroupInspector",
        logSink: _logSink,
        nodeName: GroupName,
        nodeId: NodeId);
    }

    private (ISimulatedPersonaHost? Admin, ISimulatedRelayHost? Relay) GetAdminAndRelay()
    {
        var adminMember = Members.FirstOrDefault(m => m.Role == ChannelRole.Admin) ?? Members.FirstOrDefault();
        var adminHost = adminMember != null ? _worldState.FindPersonaHostByIdentity(adminMember.IdentityId) : null;
        var relay = _worldState.GetAllRelayHosts().FirstOrDefault();
        return (adminHost, relay);
    }

    [RelayCommand]
    private async Task AddMemberAsync()
    {
        if (SelectedCandidate == null)
        {
            _toastService.ShowToast("No Candidate", "Select a persona candidate to add.", ToastSeverity.Warning);
            return;
        }

        var (admin, relay) = GetAdminAndRelay();
        if (admin == null || relay == null)
        {
            _toastService.ShowToast("Error", "Missing active group admin or relay host.", ToastSeverity.Error);
            return;
        }

        var candidate = SelectedCandidate;
        var success = await UiTaskRunner.RunAsync(
            () => _groupCoordinator.AddGroupMemberAsync(admin, relay, ChannelId, candidate),
            category: "GroupMutation",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: GroupName,
            nodeId: NodeId);

        if (success)
        {
            _flightRecorder.RecordStep(new AddGroupMemberStep(admin.DisplayName, GroupName, candidate.DisplayName));
            RefreshGroupDetails();
        }
    }

    [RelayCommand]
    private async Task EvictMemberAsync(GroupMemberItemViewModel? member)
    {
        if (member == null) return;
        var (admin, relay) = GetAdminAndRelay();
        if (admin == null || relay == null)
        {
            _toastService.ShowToast("Error", "Missing active group admin or relay host.", ToastSeverity.Error);
            return;
        }

        var memberHost = _worldState.FindPersonaHostByIdentity(member.IdentityId);
        if (memberHost == null)
        {
            _toastService.ShowToast("Not Found", "Target member persona host not found.", ToastSeverity.Warning);
            return;
        }

        var success = await UiTaskRunner.RunAsync(
            () => _groupCoordinator.EvictGroupMemberAsync(admin, relay, ChannelId, memberHost),
            category: "GroupMutation",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: GroupName,
            nodeId: NodeId);

        if (success)
        {
            _flightRecorder.RecordStep(new EvictGroupMemberStep(admin.DisplayName, GroupName, member.DisplayName));
            RefreshGroupDetails();
        }
    }

    [RelayCommand]
    private async Task RenameGroupAsync()
    {
        if (string.IsNullOrWhiteSpace(NewGroupName)) return;
        var (admin, relay) = GetAdminAndRelay();
        if (admin == null || relay == null)
        {
            _toastService.ShowToast("Error", "Missing active group admin or relay host.", ToastSeverity.Error);
            return;
        }

        var newName = NewGroupName.Trim();
        NewGroupName = string.Empty;

        var success = await UiTaskRunner.RunAsync(
            () => _groupCoordinator.RenameGroupAsync(admin, relay, ChannelId, newName),
            category: "GroupMutation",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: GroupName,
            nodeId: NodeId);

        if (success)
        {
            _flightRecorder.RecordStep(new RenameGroupStep(admin.DisplayName, GroupName, newName));
            RefreshGroupDetails();
        }
    }

    [RelayCommand]
    private async Task FloodMessagesAsync()
    {
        var (admin, _) = GetAdminAndRelay();
        if (admin == null)
        {
            _toastService.ShowToast("Error", "Missing active group admin.", ToastSeverity.Error);
            return;
        }

        var count = await UiTaskRunner.RunAsync(
            () => _groupCoordinator.FloodGroupMessagesAsync(admin, ChannelId, 25),
            category: "GroupFlood",
            logSink: _logSink,
            toastService: _toastService,
            nodeName: GroupName,
            nodeId: NodeId);

        if (count > 0)
        {
            _flightRecorder.RecordStep(new FloodGroupMessagesStep(admin.DisplayName, GroupName, 25));
            _toastService.ShowToast("Group Flood Completed", $"Dispatched {count} group messages via relay ledger.", ToastSeverity.Success);
        }
    }
}
