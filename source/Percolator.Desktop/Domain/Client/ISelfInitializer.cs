namespace Percolator.Desktop.Domain.Client;

public interface ISelfInitializer
{
    void InitSelf(Guid identitySuffix);
}