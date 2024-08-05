namespace Percolator.Desktop.Main;

public interface IChatService
{
    Task SendChatMessage(AnnouncerModel announcerModel, string text,
        CancellationToken cancellationToken = default);
}