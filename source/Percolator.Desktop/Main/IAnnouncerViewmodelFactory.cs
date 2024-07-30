using Google.Protobuf;

namespace Percolator.Desktop.Main;

public interface IAnnouncerViewmodelFactory
{
    AnnouncerViewmodel Create(AnnouncerModel announcer);
}