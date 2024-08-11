namespace Percolator.Desktop.Main;

public class ByteUtils
{
    public static int GetIntFromBytes(byte[] bytes)
    {
        var resultBytes =new byte[]{3,7,11,15};
        const int Prime = 16777619;
        for (var i = 0; i < resultBytes.Length; i++)
        {
            resultBytes[i%4] = (byte)((unchecked(bytes[i] ^ resultBytes[i % 4] * Prime)) % 255);
        }
        return BitConverter.ToInt32(resultBytes, 0);
    }
}