using System.Diagnostics.CodeAnalysis;
using System.Net;
using Percolator.Desktop.Domain.Chat;
using Percolator.Desktop.Domain.Client;

namespace Percolator.Desktop.Main;

public interface IRemoteClientService
{
    Task<bool> TrySendIntroduction(ChatModel chatModel,
        CancellationToken cancellationToken = default);
    Task<bool> TrySendReplyIntroduction(ChatModel chatModel,
        CancellationToken cancellationToken = default);
}