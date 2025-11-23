namespace Percolator.Cryptography;

public interface ISecureRandom
{
    void Fill(byte[] buffer);
    byte[] GetBytes(int count);
}

public sealed class SystemSecureRandom : ISecureRandom
{
    public void Fill(byte[] buffer)
    {
        System.Security.Cryptography.RandomNumberGenerator.Fill(buffer);
    }

    public byte[] GetBytes(int count)
    {
        return System.Security.Cryptography.RandomNumberGenerator.GetBytes(count);
    }
}
