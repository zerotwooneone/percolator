namespace Percolator.Domain.Common;

public interface ISensitiveSecret : IDisposable
{
    void Zeroize();
}
