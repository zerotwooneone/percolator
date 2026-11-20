// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Common.Navigation;

using System;
using Percolator.Domain.Identities;

/// <summary>
/// Discriminated navigation target for the Discord-inspired Left Rail.
/// </summary>
public abstract record LeftRailTarget
{
    private LeftRailTarget() { }

    /// <summary>
    /// Represents the Direct Messages mode, displaying all 1:1 direct P2P conversations in the Right Sidebar.
    /// </summary>
    public sealed record DirectMessagesTarget : LeftRailTarget
    {
        public static readonly DirectMessagesTarget Instance = new();
        public override string ToString() => "DirectMessages";
    }

    /// <summary>
    /// Represents a specific Relay (Post Office), displaying all 1:1 direct chats and group channels
    /// routing through that relay in the Right Sidebar.
    /// </summary>
    public sealed record RelayTarget(PublicIdentityId RelayIdentityId) : LeftRailTarget
    {
        public override string ToString() => $"Relay({RelayIdentityId})";
    }

    public static LeftRailTarget DirectMessages => DirectMessagesTarget.Instance;
    public static LeftRailTarget Relay(PublicIdentityId relayIdentityId) => new RelayTarget(relayIdentityId);
}

/// <summary>
/// Target kind for the right-anchored inspector slide-over panel.
/// </summary>
public enum InspectorTargetKind
{
    DirectPeer,
    Relay,
    Group
}

/// <summary>
/// Target descriptor for the right-anchored inspector slide-over panel.
/// </summary>
public sealed record InspectorTarget(InspectorTargetKind Kind, PublicIdentityId TargetId);
