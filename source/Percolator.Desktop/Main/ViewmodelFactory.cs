using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Percolator.Desktop.Main;

public class ViewmodelFactory : IAnnouncerViewmodelFactory, IChatViewmodelFactory
{
    private IAnnouncerService _announcerService;
    private readonly ILoggerFactory _loggerFactory;

    public ViewmodelFactory(
        IAnnouncerService announcerService,
        ILoggerFactory loggerFactory)
    {
        _announcerService = announcerService;
        _loggerFactory = loggerFactory;
    }

    public AnnouncerViewmodel Create(AnnouncerModel announcer)
    {
        var logger = _loggerFactory.CreateLogger<AnnouncerViewmodel>();
        return new AnnouncerViewmodel(announcer, _announcerService, logger);
    }
    
    public ChatViewmodel CreateChat(AnnouncerModel announcerModel)
    {
        return new ChatViewmodel(announcerModel);
    }
}

public interface IChatViewmodelFactory
{
    ChatViewmodel CreateChat(AnnouncerModel announcerModel);
}