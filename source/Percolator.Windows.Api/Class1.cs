using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Windows.Win32.Security.Cryptography;

namespace Percolator.WindowsX.Api;

public class Class1
{
    public void Go()
    {
        if (!TryCreateRsaAlgorithmHandle(out var alg))
        {
            return;
        }

        using var algorithmHandle = alg;
        unsafe
        {
            Span<byte> pbSecret=new byte[]{};
            Windows.Win32.PInvoke.BCryptGenerateSymmetricKey(algorithmHandle.Handle, out var publicKeyHandle, null, pbSecret, 0);
        }
    }

    internal bool TryCreateRsaAlgorithmHandle([NotNullWhen(true)]out AlgorithmHandle? algorithmHandle)
    {
        BCRYPT_ALG_HANDLE bcryptAlgHandle;
        unsafe
        {
            Windows.Win32.PInvoke.BCryptOpenAlgorithmProvider(&bcryptAlgHandle, Windows.Win32.PInvoke.BCRYPT_RSA_ALGORITHM, null, 0);

            if (bcryptAlgHandle == null)
            {
                algorithmHandle = null;
                return false;
            }
        }

        algorithmHandle = new AlgorithmHandle
        {
            Handle = bcryptAlgHandle
        };
        return true;
    }
}