using Google.Protobuf;
using R3;

namespace Percolator.Desktop.Main;

public interface IAnnouncerInitializer
{
    Observable<ByteString> AnnouncerAdded { get; }
    IReadOnlyDictionary<ByteString, AnnouncerModel> Announcers { get; }
    void AddKnownAnnouncers(IEnumerable<AnnouncerModel> announcerModels);
}