using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Domain.Chat;
using Percolator.Desktop.Domain.Client;

namespace Percolator.Desktop.Main;

public class ViewmodelFactory : IRemoteClientViewmodelFactory, IChatViewmodelFactory
{
    private IRemoteClientService _remoteClientService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IChatService _chatService;
    private readonly ChatRepository _chatRepository;

    public ViewmodelFactory(
        IRemoteClientService remoteClientService,
        ILoggerFactory loggerFactory,
        IChatService chatService,
        ChatRepository chatRepository)
    {
        _remoteClientService = remoteClientService;
        _loggerFactory = loggerFactory;
        _chatService = chatService;
        _chatRepository = chatRepository;
    }

    public RemoteClientViewmodel Create(RemoteClientModel remoteClient)
    {
        var logger = _loggerFactory.CreateLogger<RemoteClientViewmodel>();
        return new RemoteClientViewmodel(remoteClient, _remoteClientService, logger, _chatRepository);
    }
    
    public ChatViewmodel CreateChat(RemoteClientModel remoteClientModel)
    {
        if (!_chatRepository.TryGetByIdentity(remoteClientModel.Identity, out var chatModel))
        {
            chatModel = new ChatModel(remoteClientModel);
            _chatRepository.Add(chatModel);
        }
        return new ChatViewmodel(chatModel, _chatService, _loggerFactory.CreateLogger<ChatViewmodel>());
    }
}