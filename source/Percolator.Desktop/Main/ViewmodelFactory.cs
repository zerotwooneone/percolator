using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Domain.Client;

namespace Percolator.Desktop.Main;

public class ViewmodelFactory : IRemoteClientViewmodelFactory, IChatViewmodelFactory
{
    private IRemoteClientService _remoteClientService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IChatService _chatService;

    public ViewmodelFactory(
        IRemoteClientService remoteClientService,
        ILoggerFactory loggerFactory,
        IChatService chatService)
    {
        _remoteClientService = remoteClientService;
        _loggerFactory = loggerFactory;
        _chatService = chatService;
    }

    public RemoteClientViewmodel Create(RemoteClientModel remoteClient)
    {
        var logger = _loggerFactory.CreateLogger<RemoteClientViewmodel>();
        return new RemoteClientViewmodel(remoteClient, _remoteClientService, logger);
    }
    
    public ChatViewmodel CreateChat(RemoteClientModel remoteClientModel)
    {
        return new ChatViewmodel(remoteClientModel, _chatService, _loggerFactory.CreateLogger<ChatViewmodel>());
    }
}

public interface IChatViewmodelFactory
{
    ChatViewmodel CreateChat(RemoteClientModel remoteClientModel);
}