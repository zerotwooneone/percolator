using System;
using R3;

namespace Desktop.Wpf.Features.Sessions.Models;

public sealed class PendingInvitationModel : IDisposable
{
    private DisposableBag _bag;

    private readonly ReactiveProperty<string> _displayName;
    private readonly ReactiveProperty<string> _initials;
    private readonly ReactiveProperty<bool> _isRelayed;
    private readonly ReactiveProperty<DateTimeOffset> _createdAtUtc;

    public PendingInvitationModel(
        Guid pendingSessionId,
        Guid requestCorrelationId,
        string displayName,
        string initials,
        bool isRelayed,
        DateTimeOffset createdAtUtc)
    {
        PendingSessionId = pendingSessionId;
        RequestCorrelationId = requestCorrelationId;

        _displayName = new ReactiveProperty<string>(displayName);
        _initials = new ReactiveProperty<string>(initials);
        _isRelayed = new ReactiveProperty<bool>(isRelayed);
        _createdAtUtc = new ReactiveProperty<DateTimeOffset>(createdAtUtc);
    }

    public Guid PendingSessionId { get; }
    public Guid RequestCorrelationId { get; }

    public ReadOnlyReactiveProperty<string> DisplayName => _displayName;
    public ReadOnlyReactiveProperty<string> Initials => _initials;
    public ReadOnlyReactiveProperty<bool> IsRelayed => _isRelayed;
    public ReadOnlyReactiveProperty<DateTimeOffset> CreatedAtUtc => _createdAtUtc;

    internal string DisplayNameCurrent => _displayName.Value;
    internal string InitialsCurrent => _initials.Value;
    internal bool IsRelayedCurrent => _isRelayed.Value;
    internal DateTimeOffset CreatedAtUtcCurrent => _createdAtUtc.Value;

    internal void SetDisplayName(string displayName) => _displayName.Value = displayName;
    internal void SetInitials(string initials) => _initials.Value = initials;
    internal void SetRelayed(bool isRelayed) => _isRelayed.Value = isRelayed;
    internal void SetCreatedAtUtc(DateTimeOffset createdAtUtc) => _createdAtUtc.Value = createdAtUtc;

    public void Dispose() => _bag.Dispose();
}
