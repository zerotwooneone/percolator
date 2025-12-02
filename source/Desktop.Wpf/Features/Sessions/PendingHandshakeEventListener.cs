using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MediatR;
using Percolator.Chat.App.Notifications;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingHandshakeEventListener : INotificationHandler<PendingHandshakeAdded>
{
    private readonly IPendingSessionRepository _pendingRepo;
    private readonly IPeerIdentityRepository _peers;
    private readonly PendingHandshakesMenuViewModel _menu;

    public PendingHandshakeEventListener(
        IPendingSessionRepository pendingRepo,
        IPeerIdentityRepository peers,
        PendingHandshakesMenuViewModel menu)
    {
        _pendingRepo = pendingRepo;
        _peers = peers;
        _menu = menu;
    }

    public async Task Handle(PendingHandshakeAdded notification, CancellationToken cancellationToken)
    {
        var items = new List<PendingHandshakeItem>();

        await foreach (var pending in _pendingRepo.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            var pid = new Percolator.Identity.PeerId(pending.RemotePeerId.Value);
            var peer = await _peers.GetByIdAsync(pid, cancellationToken).ConfigureAwait(false);
            var name = peer?.DisplayName?.Value ?? pid.Value.ToString()[..8];
            items.Add(new PendingHandshakeItem
            {
                DisplayName = name,
                Initials = ComputeInitials(name),
                BundleText = $"Proto v{pending.ProtocolVersion.Value} • Created {pending.CreatedAtUtc:HH:mm:ss}"
            });
        }

        void apply()
        {
            _menu.PendingHandshakes.Clear();
            foreach (var it in items)
                _menu.PendingHandshakes.Add(it);
        }

        if (Application.Current?.Dispatcher is { } d)
        {
            await d.InvokeAsync(apply);
        }
        else
        {
            apply();
        }
    }

    private static string ComputeInitials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Substring(0, Math.Min(2, parts[0].Length)).ToUpperInvariant();
        return (parts[0][0].ToString() + parts[^1][0].ToString()).ToUpperInvariant();
    }
}
