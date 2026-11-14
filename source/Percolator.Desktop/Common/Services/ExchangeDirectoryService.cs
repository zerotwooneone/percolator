// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
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
/// Default <see cref="IExchangeDirectory"/>: opens a DI scope on the authenticated session
/// and drives exchange operations through the vault's relay directory. Gate-time link,
/// unlink, and create operations are pure vault writes — zero wire traffic
/// ([DEP-DESKTOP-PRE-ACTIVATION-STEALTH]). Launch-time operations (endpoint start,
/// mailbox provisioning) are the only calls that can reach the network.
/// </summary>
public sealed class ExchangeDirectoryService : IExchangeDirectory
{
    /// <summary>Default preferred port for self-hosted exchanges (above the persona range start).</summary>
    public const int DefaultPreferredPort = 5200;

    private const string SelfHostedBindHost = "127.0.0.1";

    private readonly IDesktopSettingsRepository _settings;

    public ExchangeDirectoryService(IDesktopSettingsRepository settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<IReadOnlyList<ExchangeDirectoryEntry>> ListAsync(
        IAuthenticatedSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var scope = CreateScope(session);
        var relays = await scope.ServiceProvider
            .GetRequiredService<IRelayDirectory>()
            .ListAsync(ct)
            .ConfigureAwait(false);
        var settings = (await _settings.GetAllAsync(ct).ConfigureAwait(false))
            .ToDictionary(s => s.IdentityId);

        return relays.Select(r => ToEntry(r, settings)).ToList();
    }

    public async Task<ExchangeDirectoryEntry> CreateSelfHostedAsync(
        IAuthenticatedSession session,
        PublicIdentityId relayIdentityId,
        string displayName,
        int preferredPort,
        IReadOnlyList<PublicIdentityId> initialLinkedPersonaIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(initialLinkedPersonaIds);

        var trimmed = (displayName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Display name cannot be whitespace.", nameof(displayName));
        }

        var endpoint = RelayEndpoint.FromHostAndPort(SelfHostedBindHost, preferredPort);

        using var scope = CreateScope(session);
        var directory = scope.ServiceProvider.GetRequiredService<IRelayDirectory>();
        var result = await directory
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
            new DesktopIdentitySettingsEntity(relayIdentityId.Value, preferredPort, null, false, order),
            ct).ConfigureAwait(false);

        return ToEntry(result.Value!, new Dictionary<Guid, DesktopIdentitySettingsEntity>());
    }

    public async Task<ExchangeDirectoryEntry> RegisterRemoteAsync(
        IAuthenticatedSession session,
        PublicIdentityId relayIdentityId,
        string endpoint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var endpointResult = RelayEndpoint.Create(endpoint);
        if (endpointResult.IsFailure)
        {
            throw new ArgumentException(endpointResult.Error.Description, nameof(endpoint));
        }

        using var scope = CreateScope(session);
        var result = await scope.ServiceProvider
            .GetRequiredService<IRelayDirectory>()
            .RegisterRemoteRelayAsync(relayIdentityId, endpointResult.Value!, ct: ct)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error.Description);
        }

        return ToEntry(result.Value!, new Dictionary<Guid, DesktopIdentitySettingsEntity>());
    }

    public async Task<DomainResult> UpdateSelfHostedProfileAsync(
        IAuthenticatedSession session,
        PublicIdentityId relayIdentityId,
        string displayName,
        int preferredPort,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var trimmed = (displayName ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return DomainResult.Failure(new DomainError(
                "INVALID_DISPLAY_NAME", "Display name cannot be whitespace."));
        }

        using var scope = CreateScope(session);
        var profiles = scope.ServiceProvider.GetRequiredService<IIdentityProfileRepository>();
        var existing = await profiles.GetByIdAsync(relayIdentityId, ct).ConfigureAwait(false);
        if (existing is null || existing.Role != IdentityRole.RelayHost)
        {
            return DomainResult.Failure(new DomainError(
                "RELAY_NOT_SELF_HOSTED", "Only self-hosted exchanges have an editable profile."));
        }

        // IdentityProfile has no rename member; rehydrate preserves id/role/state/dates.
        var renamed = IdentityProfile.Rehydrate(
            existing.Id, trimmed, existing.Role, existing.State,
            existing.CreatedAtUtc, existing.DisabledAtUtc);
        await profiles.SaveAsync(renamed, ct).ConfigureAwait(false);

        var settingsEntity = await _settings.GetAsync(relayIdentityId.Value, ct).ConfigureAwait(false)
            ?? new DesktopIdentitySettingsEntity(relayIdentityId.Value, preferredPort, null, false, 0);
        await _settings.SaveAsync(settingsEntity with { PreferredPort = preferredPort }, ct)
            .ConfigureAwait(false);

        return DomainResult.Success();
    }

    public async Task<DomainResult> LinkPersonaAsync(
        IAuthenticatedSession session,
        PublicIdentityId relayIdentityId,
        PublicIdentityId personaId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var scope = CreateScope(session);
        var result = await scope.ServiceProvider
            .GetRequiredService<IRelayDirectory>()
            .LinkIdentityAsync(relayIdentityId, personaId, ct: ct)
            .ConfigureAwait(false);
        return result.IsFailure ? DomainResult.Failure(result.Error) : DomainResult.Success();
    }

    public async Task<DomainResult> UnlinkPersonaAsync(
        IAuthenticatedSession session,
        PublicIdentityId relayIdentityId,
        PublicIdentityId personaId,
        bool purgeQueuedEnvelopes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var scope = CreateScope(session);
        var directory = scope.ServiceProvider.GetRequiredService<IRelayDirectory>();

        if (purgeQueuedEnvelopes)
        {
            // Self-hosted exchange: the relay queue lives in this same vault — purge is
            // verifiable local deletion. Tokens must be read before the binding is removed.
            var tokensResult = await directory
                .GetMailboxTokensAsync(personaId, relayIdentityId, ct)
                .ConfigureAwait(false);
            if (tokensResult.IsSuccess && tokensResult.Value is { } tokens)
            {
                var capabilities = scope.ServiceProvider
                    .GetRequiredService<IRelayMailboxCapabilityStore>();
                await capabilities
                    .DrainEnvelopesAsync(tokens.RoutingToken.ToByteArray(), ct)
                    .ConfigureAwait(false);
            }
        }

        return await directory
            .UnlinkIdentityAsync(relayIdentityId, personaId, ct)
            .ConfigureAwait(false);
    }

    public async Task<int?> StartSelfHostedEndpointAsync(
        IAuthenticatedSession session,
        ExchangeLaunchDescriptor exchange,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(exchange);

        using var scope = CreateScope(session);
        var transport = scope.ServiceProvider.GetRequiredService<IPeerTransportServer>();
        if (transport.IsRunning)
        {
            // The session owns a single transport singleton — a second self-hosted exchange
            // shares the already-bound listener rather than failing the launch.
            return transport.BoundPort?.Value;
        }

        await transport
            .StartAsync(IPAddress.Loopback, new PortNumber(exchange.RequestedPort), mapRelayEndpoints: true, ct)
            .ConfigureAwait(false);
        return transport.BoundPort?.Value;
    }

    public async Task<DomainResult> ProvisionLinkedMailboxesAsync(
        IAuthenticatedSession session,
        IEnumerable<PublicIdentityId> personaIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(personaIds);

        using var scope = CreateScope(session);
        var directory = scope.ServiceProvider.GetRequiredService<IRelayDirectory>();

        var failures = new List<string>();
        foreach (var personaId in personaIds)
        {
            var result = await directory
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

    private static IServiceScope CreateScope(IAuthenticatedSession session) =>
        session.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();

    private static ExchangeDirectoryEntry ToEntry(
        RelayDirectoryEntry relay,
        IReadOnlyDictionary<Guid, DesktopIdentitySettingsEntity> settings) =>
        new(
            relay.RelayIdentityId.Value,
            relay.DisplayName,
            relay.Endpoint.Value,
            settings.TryGetValue(relay.RelayIdentityId.Value, out var entity)
                ? entity.PreferredPort
                : relay.Endpoint.Port ?? DefaultPreferredPort,
            relay.IsLocalRelayHost,
            relay.LinkedOwnerIdentityIds.Select(id => id.Value).ToList());
}
