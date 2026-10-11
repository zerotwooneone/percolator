using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;

namespace Percolator.Apps.Chat.Features.Messaging;

public interface IChatService
{
    ValueTask<DomainResult<PayloadId>> SendMessageAsync(SendMessageCommand command, CancellationToken ct = default);
    ValueTask<DomainResult> EditMessageAsync(EditMessageCommand command, CancellationToken ct = default);
    ValueTask<DomainResult> DeleteMessageAsync(DeleteMessageCommand command, CancellationToken ct = default);
    ValueTask<DomainResult> AddReactionAsync(AddReactionCommand command, CancellationToken ct = default);
    ValueTask<DomainResult> RemoveReactionAsync(RemoveReactionCommand command, CancellationToken ct = default);
    ValueTask<DomainResult> MarkAsReadAsync(MarkAsReadCommand command, CancellationToken ct = default);
}
