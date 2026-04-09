namespace Desktop.Wpf.Features.Simulator;

public enum SimulatorPeerUiState
{
    Ready = 0,
    OutboundPending = 1,
    AwaitingUserAcceptance = 2,
    Established = 3,
    Offline = 4,
    Expired = 5
}