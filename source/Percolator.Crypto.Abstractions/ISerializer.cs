namespace Percolator.Crypto;

public interface ISerializer
{
    byte[] Serialize(HeaderWrapper header);

    HeaderWrapper? Deserialize(byte[] bytes);
}