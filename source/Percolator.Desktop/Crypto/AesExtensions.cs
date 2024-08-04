using System.IO;
using System.Security.Cryptography;

namespace Percolator.Desktop.Crypto;

public static class AesExtensions
{
    public static async Task<byte[]> NaiveDecrypt(this Aes aes, byte[] encrypted)
    {
        using MemoryStream encryptedStream = new MemoryStream(encrypted);
        await using CryptoStream cs = new CryptoStream(encryptedStream, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using MemoryStream decryptedStream = new MemoryStream();
        await cs.CopyToAsync(decryptedStream);
        return decryptedStream.ToArray();
    }
    
    public static async Task<byte[]> NaiveEncrypt(this Aes aes, byte[] plaintext)
    {
        using var chatEncrypted = new MemoryStream();
        await using CryptoStream chatCs = new CryptoStream(chatEncrypted, aes.CreateEncryptor(), CryptoStreamMode.Write);
        await chatCs.WriteAsync(plaintext);
        await chatCs.FlushFinalBlockAsync();
        return chatEncrypted.ToArray();
    }
}