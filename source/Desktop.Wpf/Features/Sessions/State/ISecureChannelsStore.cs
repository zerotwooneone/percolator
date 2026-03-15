using System;
using System.Collections.ObjectModel;
using Desktop.Wpf.Features.Sessions.Models;
using R3;

namespace Desktop.Wpf.Features.Sessions.State;

public interface ISecureChannelsStore
{
    ReadOnlyObservableCollection<SecureChannelModel> Channels { get; }

    ReadOnlyObservableCollection<PendingInvitationModel> PendingInbound { get; }

    ReadOnlyReactiveProperty<int> PendingInboundCount { get; }
}
