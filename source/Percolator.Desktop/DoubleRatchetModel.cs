using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;
using Google.Protobuf;
using Percolator.Protobuf.DoubleRatchet;

namespace Percolator.Desktop;

public class DoubleRatchetModel
{
    private readonly byte[] MessageTypeId = [1];
    private readonly byte[] ChainKeyTypeId = [2];

    public ECDiffieHellman DHs { get; private set; }
    public ECDiffieHellmanPublicKey? DHr { get; private set; }
    /// <summary>
    /// 32 byte 
    /// </summary>
    public byte[] RootKey { get; private set; }

    /// <summary>
    /// 32 byte 
    /// </summary>
    public byte[]? CKs { get; private set; }
    /// <summary>
    /// 32 byte 
    /// </summary>
    public byte[]? CKr { get; private set; }
    
    /// <summary>
    /// message number for sending
    /// </summary>
    public uint Ns { get; private set; }
    /// <summary>
    /// message number for receiving
    /// </summary>
    public uint Nr { get; private set; }

    /// <summary>
    /// number of messages in previous sending chain
    /// </summary>
    public uint PN { get; private set; }
    
    private ConcurrentDictionary<MKSkippedKey, byte[]> MKSkipped { get; } = new(); 
    public readonly uint MaxSkip;
    public static readonly byte[] KDF_RK_Info = [3, 7, 9, 13];
    
    private DoubleRatchetModel(
        ECDiffieHellman dHs, 
        ECDiffieHellmanPublicKey? dHr, 
        byte[] rootKey,
        uint maxSkip = 1000)
    {
        MaxSkip = maxSkip;
        DHs = dHs;
        DHr = dHr;
        RootKey = rootKey;
    }

    public static DoubleRatchetModel CreateSender(ECDiffieHellmanPublicKey otherPublicKey,
        byte[] secretKey)
    {
        var dHs = ECDiffieHellman.Create();
        var (rootKey, chainKey) = KDF_RK(secretKey, dHs.DeriveKeyMaterial(otherPublicKey), KDF_RK_Info);
        var model = new DoubleRatchetModel(dHs, otherPublicKey, rootKey);
        model.CKs = chainKey;
        return model;
    }
    
    public static DoubleRatchetModel CreateReceiver(ECDiffieHellman dHs,
        byte[] secretKey)
    {
        return new DoubleRatchetModel(dHs, null, secretKey);
    }
    
    public  bool TrySetOtherPublicKey(ECDiffieHellmanPublicKey otherPublicKey)
    {
        if (DHr != null) return false;
        DHr = otherPublicKey;
        return true;
    }

    private byte[] DH(ECDiffieHellman key)
    {
        return key.DeriveKeyMaterial(DHr);
    }
    private static (byte[] rootKey, byte[] chainKey) KDF_RK(byte[] salt, byte[] inputKeyMaterial, byte[] info, int length=32) {
        byte[] key = HKDF.Extract(
            HashAlgorithmName.SHA256, 
            inputKeyMaterial,salt);
        var chainKey = HKDF.Expand(
            HashAlgorithmName.SHA256,
            key,length,info);
        return(key,chainKey);
    }
    
    private (byte[] chainKey, byte[] messageKey) KDF_CK(byte[] key)
    {
        return ( HMACSHA512.HashData(key,MessageTypeId), HMACSHA512.HashData(key,ChainKeyTypeId) );
    }

    public static readonly byte[] Nonce = [3,7,11,15,19,23,27,31,35,39,43,47]; //must be 12 bytes
    
    private (byte[] header, byte[] encrypted, byte[] headerSignature) RatchetEncrypt(byte[] plainText, byte[] associatedData)
    {
        var (nextCKs, mk) = KDF_CK(CKs);
        CKs = nextCKs;
        var header = new Percolator.Protobuf.DoubleRatchet.encryptionWrapper
        {
            Header = new Percolator.Protobuf.DoubleRatchet.encryptionWrapper.Types.Header
            {
                PublicKey = ByteString.CopyFrom(DHs.ExportSubjectPublicKeyInfo()),
                PreviousChainLength = PN,
                MessageNumber = Ns
            },
            AssociatedData = new Percolator.Protobuf.DoubleRatchet.encryptionWrapper.Types.AssociatedData
            {
                Data = ByteString.CopyFrom(associatedData)
            }
        };
        Ns++;
        var headerBytes = header.ToByteArray();
        var (encrypted, headerSignature) = Encrypt(mk, plainText, headerBytes);
        return (headerBytes, encrypted, headerSignature);
    }

    private static readonly byte[] EncryptInfo = Guid.Parse("3c89ac43-b852-4291-8d08-b106f3a192ee").ToByteArray();
    private (byte[] cipherText, byte[] associatedDataSignature) Encrypt(byte[] messageKey, byte[] plainText, byte[] associatedData)
    {
        var extracted = HKDF.Extract(HashAlgorithmName.SHA256, messageKey);
        var expanded = HKDF.Expand(HashAlgorithmName.SHA256, extracted, 80, EncryptInfo);
        
        var encryptionKey = expanded.Take(32).ToArray();
        var authenticationKey = expanded.Skip(32).Take(32).ToArray();
        var iv = expanded.Skip(64).Take(16).ToArray();

        using var aesCbc = Aes.Create();
        aesCbc.Key = encryptionKey;
        aesCbc.IV = iv;
        aesCbc.Mode = CipherMode.CBC;
        aesCbc.Padding = PaddingMode.PKCS7;
        using ICryptoTransform encryptor = aesCbc.CreateEncryptor();
        var encryptedBytes = encryptor.TransformFinalBlock(plainText, 0, plainText.Length);
        
        var hmac = new HMACSHA256(authenticationKey);
        var signed = hmac.ComputeHash(associatedData.Concat(encryptedBytes).ToArray());
        
        return (encryptedBytes, signed);
    }
    
    private byte[] Decrypt(byte[] messageKey, byte[] cipherText, encryptionWrapper.Types.AssociatedData associatedData,
        byte[] headerSignature)
    {
        var extracted = HKDF.Extract(HashAlgorithmName.SHA256, messageKey);
        var expanded = HKDF.Expand(HashAlgorithmName.SHA256, extracted, 80, EncryptInfo);
        
        var authenticationKey = expanded.Skip(32).Take(32).ToArray();
        var hmac = new HMACSHA256(authenticationKey);
        var associatedDataBytes = associatedData.ToByteArray();
        var actualSignature = ByteString.CopyFrom(hmac.ComputeHash(associatedDataBytes.Concat(cipherText).ToArray()));
        if (!actualSignature.Equals(headerSignature))
        {
            throw new CryptographicException("signature mismatch");
        }
        
        var encryptionKey = expanded.Take(32).ToArray();
        var iv = expanded.Skip(64).Take(16).ToArray();

        using var aesCbc = Aes.Create();
        aesCbc.Key = encryptionKey;
        aesCbc.IV = iv;
        aesCbc.Mode = CipherMode.CBC;
        aesCbc.Padding = PaddingMode.PKCS7;
        using var decryptor = aesCbc.CreateDecryptor();
        return decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
    }

    private string? RatchetDecrypt(byte[] headerBytes, byte[] cipherText, byte[] headerSignature)
    {
        var headerWrapper = encryptionWrapper.Parser.ParseFrom(headerBytes);
        if (headerWrapper == null)
        {
            return null;
        }

        if (headerWrapper.Header == null || !headerWrapper.Header.HasMessageNumber || !headerWrapper.Header.HasPublicKey || !headerWrapper.Header.HasPreviousChainLength)
        {
            return null;
        }

        if (headerWrapper.AssociatedData == null || !headerWrapper.AssociatedData.HasData)
        {
            return null;
        }

        var currentSkippedKey = new MKSkippedKey
        {
            MessageNumber = headerWrapper.Header.MessageNumber,
            MessagePublicKey = headerWrapper.Header.PublicKey
        };
        if (TrySkippedMessageKeys(currentSkippedKey, cipherText, headerWrapper.AssociatedData,headerSignature, out var skippedBytes))
        {
            return Encoding.UTF8.GetString(skippedBytes);
        }
        
        //todo:avoid the array copy here
        if (!ByteString.CopyFrom(DHr.ExportSubjectPublicKeyInfo()).Equals(headerWrapper.Header.PublicKey))
        {
            SkipMessageKeys(headerWrapper.Header.PreviousChainLength,currentSkippedKey);
            DHRatchet(headerWrapper.Header);
        }
        SkipMessageKeys(headerWrapper.Header.MessageNumber,currentSkippedKey);
        var (nextCKr, mk) = KDF_CK(CKr);
        Nr++;
        CKr = nextCKr;
        var decrypted = Decrypt(mk, cipherText, headerWrapper.AssociatedData,headerSignature);
        return Encoding.UTF8.GetString(decrypted);
    }

    private void DHRatchet(encryptionWrapper.Types.Header header)
    {
        PN = Ns;
        Ns = 0;
        Nr = 0;
        var publicKey = ECDiffieHellmanCngPublicKey.FromByteArray(header.PublicKey.ToByteArray(),CngKeyBlobFormat.EccPublicBlob);
        
        
        DHr = publicKey;
        var (rootKeyR, ckr) = KDF_RK(RootKey, DHs.DeriveKeyMaterial(publicKey), KDF_RK_Info);
        RootKey = rootKeyR;
        CKr = ckr;
        
        DHs = ECDiffieHellman.Create();
        var (rootKeyS, cks) = KDF_RK(RootKey, DHs.DeriveKeyMaterial(publicKey), KDF_RK_Info);
        RootKey = rootKeyS;
        CKs = cks;
    }

    private void SkipMessageKeys(
        uint until,
        MKSkippedKey currentSkippedKey)
    {
        if (Nr + MaxSkip < until)
        {
            throw new Exception("too much skips");
        }

        if (CKr == null)
        {
            return;
        }

        while (Nr < until)
        {
            var (nextCKr, mk) = KDF_CK(CKr);
            CKr = nextCKr;
            MKSkipped[currentSkippedKey]= mk;
            Nr++;
        }
    }

    private bool TrySkippedMessageKeys(
        MKSkippedKey currentSkippedKey,
        byte[] cipherText,
        encryptionWrapper.Types.AssociatedData associatedData, 
        byte[] headerSignature, 
        [NotNullWhen(true)] out byte[]? decrypted)
    {
        if (!MKSkipped.Remove(currentSkippedKey, out var mk))
        {
            decrypted = null;
            return false;
        }
        decrypted = Decrypt(mk, cipherText, associatedData,headerSignature);
        return true;
    }
    
}

public record MKSkippedKey
{
    public uint MessageNumber { get; init; }
    public ByteString MessagePublicKey { get; init; }
}