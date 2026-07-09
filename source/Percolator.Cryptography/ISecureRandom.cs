namespace Percolator.Cryptography;

public interface ISecureRandom
{
    byte[] GetBytes(int count);
}

public sealed class SystemSecureRandom : ISecureRandom
{
    public byte[] GetBytes(int count)
    {
        return System.Security.Cryptography.RandomNumberGenerator.GetBytes(count);
    }
}
