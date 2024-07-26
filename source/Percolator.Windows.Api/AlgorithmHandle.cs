using Windows.Win32.Security.Cryptography;

namespace Percolator.WindowsX.Api;

internal class AlgorithmHandle : IDisposable
{
    public BCRYPT_ALG_HANDLE Handle { get; init; }

    private void ReleaseUnmanagedResources()
    {
        Windows.Win32.PInvoke.BCryptCloseAlgorithmProvider(Handle, 0);
    }

    public void Dispose()
    {
        ReleaseUnmanagedResources();
        GC.SuppressFinalize(this);
    }

    ~AlgorithmHandle()
    {
        ReleaseUnmanagedResources();
    }
}