using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Percolator.Desktop.Main;

public class ViewmodelFactory : IAnnouncerViewmodelFactory, IChatViewmodelFactory
{
    private IAnnouncerService _announcerService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IChatService _chatService;

    public ViewmodelFactory(
        IAnnouncerService announcerService,
        ILoggerFactory loggerFactory,
        IChatService chatService)
    {
        _announcerService = announcerService;
        _loggerFactory = loggerFactory;
        _chatService = chatService;
    }

    public AnnouncerViewmodel Create(AnnouncerModel announcer)
    {
        var logger = _loggerFactory.CreateLogger<AnnouncerViewmodel>();
        return new AnnouncerViewmodel(announcer, _announcerService, logger);
    }
    
    public ChatViewmodel CreateChat(AnnouncerModel announcerModel)
    {
        return new ChatViewmodel(announcerModel, _chatService, _loggerFactory.CreateLogger<ChatViewmodel>());
    }
}

public interface IChatViewmodelFactory
{
    ChatViewmodel CreateChat(AnnouncerModel announcerModel);
}