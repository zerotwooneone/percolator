namespace Desktop.Wpf.Features.Simulator;

public enum SimulatorDiagnosticEventType
{
    PeerCreated = 0,
    PeerRemoved = 1,
    PeerOnlineChanged = 2,
    PeerRelayCapableChanged = 3,
    PreKeyPublishRelationshipAdded = 4,
    PreKeyPublishRelationshipRemoved = 5,
    HandshakeStateTransition = 6,
    RelayEnqueued = 7,
    RelayDelivered = 8,
    RelayDropped = 9,
    RelayCorrupted = 10,
    RelayReordered = 11,
    DecryptFailure = 12,
    PreKeyBundleFetched = 13,
    StandardHandshakeHelloEnqueued = 14,
    RelayRoutingFailure = 15,
    RelayActiveSessionAdded = 16,
    RelayActiveSessionRemoved = 17,
    PreKeyPublishBlockedMissingActiveSession = 18,
    HandshakeError = 19
}