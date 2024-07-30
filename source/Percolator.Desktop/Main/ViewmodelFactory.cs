using Google.Protobuf;

namespace Percolator.Desktop.Main;

public class ViewmodelFactory : IAnnouncerViewmodelFactory
{
    public AnnouncerViewmodel Create(AnnouncerModel announcer)
    {
        return new AnnouncerViewmodel(announcer);
    }
}