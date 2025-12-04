using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MediatR;
using Percolator.Application.Cryptography;
using Percolator.Chat.App.Notifications;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Desktop.Wpf.Features.Sessions;

public sealed class PendingHandshakeEventListener : INotificationHandler<PendingHandshakeAdded>
{
    private readonly IPendingSessionRepository _pendingRepo;
    private readonly IPeerIdentityRepository _peers;
    private readonly PendingHandshakesMenuViewModel _menu;
    private readonly IPendingHandshakeQueries _pendingHandshakeQueries;

    public PendingHandshakeEventListener(
        IPendingSessionRepository pendingRepo,
        IPeerIdentityRepository peers,
        PendingHandshakesMenuViewModel menu, 
        IPendingHandshakeQueries pendingHandshakeQueries)
    {
        _pendingRepo = pendingRepo;
        _peers = peers;
        _menu = menu;
        _pendingHandshakeQueries = pendingHandshakeQueries;
    }

    public async Task Handle(PendingHandshakeAdded notification, CancellationToken cancellationToken)
    {
        var items = new List<PendingHandshakeItem>();

        await foreach (var pending in _pendingHandshakeQueries.EnumerateOpenAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new PendingHandshakeItem
            {
                DisplayName = pending.PeerName,
                Initials = ComputeInitials(pending.PeerName),
                BundleText = $"bundle text",
                PendingId = pending.Id
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
