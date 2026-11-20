// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/UiStateService.spec.md before modifying this file.

namespace Percolator.Desktop.Features.Chats.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Identities;
using Percolator.Infrastructure.Persistence.Repositories;

/// <summary>
/// Singleton facade for <see cref="IConversationListQueryService"/> in the Desktop session container.
/// Executes queries within an isolated service scope to prevent captive-dependency leaks
/// when called from singleton UI state services or root-level consumers.
/// </summary>
public sealed class DesktopConversationListQueryService : IConversationListQueryService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DesktopConversationListQueryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task<IReadOnlyList<ConversationSummaryReadModel>> GetConversationsAsync(
        PublicIdentityId currentUserId,
        ulong currentTimestampMs,
        CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var inner = scope.ServiceProvider.GetService<SqlConversationListQueryService>()
            ?? scope.ServiceProvider.GetServices<IConversationListQueryService>().FirstOrDefault(s => s is not DesktopConversationListQueryService);

        if (inner == null)
        {
            return Array.Empty<ConversationSummaryReadModel>();
        }

        return await inner.GetConversationsAsync(currentUserId, currentTimestampMs, ct).ConfigureAwait(false);
    }
}
