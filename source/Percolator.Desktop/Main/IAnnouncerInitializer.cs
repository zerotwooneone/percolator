namespace Percolator.Desktop.Main;

public interface IAnnouncerInitializer
{
    void AddKnownAnnouncers(IEnumerable<AnnouncerModel> announcerModels);
}