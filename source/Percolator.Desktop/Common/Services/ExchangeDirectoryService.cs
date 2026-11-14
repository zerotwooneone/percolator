// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Application.Delivery.Ports;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Storage;
using Percolator.Desktop.Features.Startup.Models;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Transport;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Default <see cref="IExchangeDirectory"/>: resolved from the authenticated session
/// container, so the vault's relay directory and transport arrive as constructor
/// dependencies. Gate-time link, unlink, and create operations are pure vault writes —
/// zero wire traffic ([DEP-DESKTOP-PRE-ACTIVATION-STEALTH]). Launch-time operations
/// (endpoint start, mailbox provisioning) are the only calls that can reach the network.
/// </summary>
public sealed class ExchangeDirectoryService : IExchangeDirectory
{
    /// <summary>Default preferred port for self-hosted exchanges (above the persona range start).</summary>
    public const int DefaultPreferredPort = 5200;

    private const string SelfHostedBindHost = "127.0.0.1";

    private readonly IRelayDirectory _directory;
    private readonly IIdentityProfileRepository _profiles;
    private readonly IRelayMailboxCapabilityStore _capabilities;
    private readonly IPeerTransportServer _transport;
    private readonly IDesktopSettingsRepository _settings;

    public ExchangeDirectoryService(
        IRelayDirectory directory,
        IIdentityProfileRepository profiles,
        IRelayMailboxCapabilityStore capabilities,
        IPeerTransportServer transport,
        IDesktopSettingsRepository settings)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<IReadOnlyList<ExchangeDirectoryEntry>> ListAsync(
        CancellationToken ct = default)
    {
        var relays = await _directory.ListAsync(ct).ConfigureAwait(false);
        var settings = (await _settings.GetAllAsync(ct).ConfigureAwait(false))
            .ToDictionary(s => s.IdentityId, s => s.Settings);

        return relays.Select(r => ToEntry(r, settings)).ToList();
    }

    public async Task<ExchangeDirectoryEntry> CreateSelfHostedAsync(
        PublicIdentityId relayIdentityId,
        string displayName,
        int preferredPort,
        IReadOnlyList<PublicIdentityId> initialLinkedPersonaIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(initialLinkedPersonaIds);

        var trimmed = (displayName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Display name cannot be whitespace.", nameof(displayName));
        }

        var endpoint = RelayEndpoint.FromHostAndPort(SelfHostedBindHost, preferredPort);

        var result = await _directory
            .CreateLocalRelayHostAsync(relayIdentityId, trimmed, endpoint, initialLinkedPersonaIds, ct)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Description);
        }

        // Reuse the per-identity settings row to persist the launch port preference —
        // relay ids are drawn from the same space as persona ids, so there is no collision.
        var existing = await _settings.GetAsync(relayIdentityId.Value, ct).ConfigureAwait(false);
        var order = existing?.SidebarOrder
            ?? (await _settings.GetAllAsync(ct).ConfigureAwait(false)).Count;
        await _settings.SaveAsync(
            relayIdentityId.Value,
            new IdentityLaunchSettings(preferredPort, null, false, order),
            ct).ConfigureAwait(false);

        return ToEntry(result.Value!, new Dictionary<PublicIdentityId, IdentityLaunchSettings>());
    }

    public async Task<ExchangeDirectoryEntry> RegisterRemoteAsync(
        PublicIdentityId relayIdentityId,
        string endpoint,
        CancellationToken ct = default)
    {
        var endpointResult = RelayEndpoint.Create(endpoint);
        if (endpointResult.IsFailure)
        {
            throw new ArgumentException(endpointResult.Error.Description, nameof(endpoint));
        }

        var result = await _directory
            .RegisterRemoteRelayAsync(relayIdentityId, endpointResult.Value!, ct: ct)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Description);
        }

        return ToEntry(result.Value!, new Dictionary<PublicIdentityId, IdentityLaunchSettings>());
    }

    public async Task<DomainResult> UpdateSelfHostedProfileAsync(
        PublicIdentityId relayIdentityId,
        string displayName,
        int preferredPort,
        CancellationToken ct = default)
    {
        var trimmed = (displayName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_DISPLAY_NAME", "Display name cannot be whitespace."));
        }

        var existing = await _profiles.GetByIdAsync(relayIdentityId, ct).ConfigureAwait(false);
        if (existing is null || existing.Role != IdentityRole.RelayHost)
        {
            return DomainResult.Failure(new DomainError(
                "RELAY_NOT_SELF_HOSTED", "Only self-hosted exchanges have an editable profile."));
        }

        // IdentityProfile has no rename member; rehydrate preserves id/role/state/dates.
        var renamed = IdentityProfile.Rehydrate(
            existing.Id, trimmed, existing.Role, existing.State,
            existing.CreatedAtUtc, existing.DisabledAtUtc);
        await _profiles.SaveAsync(renamed, ct).ConfigureAwait(false);

        var settingsEntity = await _settings.GetAsync(relayIdentityId.Value, ct).ConfigureAwait(false)
            ?? new IdentityLaunchSettings(preferredPort, null, false, 0);
        await _settings.SaveAsync(
                relayIdentityId.Value,
                settingsEntity with { PreferredPort = preferredPort },
                ct)
            .ConfigureAwait(false);

        return DomainResult.Success();
    }

    public async Task<DomainResult> LinkPersonaAsync(
        PublicIdentityId relayIdentityId,
        PublicIdentityId personaId,
        CancellationToken ct = default)
    {
        var result = await _directory
            .LinkIdentityAsync(relayIdentityId, personaId, ct: ct)
            .ConfigureAwait(false);
        return result.IsFailure ? DomainResult.Failure(result.Error) : DomainResult.Success();
    }

    public async Task<DomainResult> UnlinkPersonaAsync(
        PublicIdentityId relayIdentityId,
        PublicIdentityId personaId,
        bool purgeQueuedEnvelopes,
        CancellationToken ct = default)
    {
        if (purgeQueuedEnvelopes)
        {
            // Self-hosted exchange: the relay queue lives in this same vault — purge is
            // verifiable local deletion. Tokens must be read before the binding is removed.
            var tokensResult = await _directory
                .GetMailboxTokensAsync(personaId, relayIdentityId, ct)
                .ConfigureAwait(false);
            if (tokensResult.IsSuccess && tokensResult.Value is { } tokens)
            {
                await _capabilities
                    .DrainEnvelopesAsync(tokens.RoutingToken.ToByteArray(), ct)
                    .ConfigureAwait(false);
            }
        }

        return await _directory
            .UnlinkIdentityAsync(relayIdentityId, personaId, ct)
            .ConfigureAwait(false);
    }

    public async Task<int?> StartSelfHostedEndpointAsync(
        ExchangeLaunchDescriptor exchange,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exchange);

        if (_transport.IsRunning)
        {
            // The session owns a single transport singleton — a second self-hosted exchange
            // shares the already-bound listener rather than failing the launch.
            return _transport.BoundPort?.Value;
        }

        await _transport
            .StartAsync(IPAddress.Loopback, new PortNumber(exchange.RequestedPort), mapRelayEndpoints: true, ct)
            .ConfigureAwait(false);
        return _transport.BoundPort?.Value;
    }

    public async Task<DomainResult> ProvisionLinkedMailboxesAsync(
        IEnumerable<PublicIdentityId> personaIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(personaIds);

        var failures = new List<string>();
        foreach (var personaId in personaIds)
        {
            var result = await _directory
                .ProvisionLinkedMailboxesAsync(personaId, ct)
                .ConfigureAwait(false);
            if (result.IsFailure)
            {
                failures.Add($"{personaId.Value}: {result.Error.Description}");
            }
        }

        return failures.Count == 0
            ? DomainResult.Success()
            : DomainResult.Failure(new DomainError(
                "MAILBOX_PROVISION_FAILED",
                $"Mailbox provisioning failed for {failures.Count} persona(s): {string.Join("; ", failures)}"));
    }

    private static ExchangeDirectoryEntry ToEntry(
        RelayDirectoryEntry relay,
        IReadOnlyDictionary<PublicIdentityId, IdentityLaunchSettings> settings) =>
        new(
            relay.RelayIdentityId.Value,
            relay.DisplayName,
            relay.Endpoint.Value,
            settings.TryGetValue(relay.RelayIdentityId, out var entity)
                ? entity.PreferredPort
                : relay.Endpoint.Port ?? DefaultPreferredPort,
            relay.IsLocalRelayHost,
            relay.LinkedOwnerIdentityIds.Select(id => id.Value).ToList());
}
