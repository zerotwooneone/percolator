using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Percolator.Crypto;

public class DoubleRatchetModel
{
    private static readonly ArrayEqualityComparer<byte> ArrayEqualityComparer = new();
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
    private readonly ILogger<DoubleRatchetModel> _logger;
    private readonly ISerializer _serializer;
    public readonly uint MaxSkip;
    public static readonly byte[] KDF_RK_Info = [3, 7, 9, 13];
    
    private DoubleRatchetModel(
        ECDiffieHellman dHs, 
        ECDiffieHellmanPublicKey? dHr, 
        byte[] rootKey,
        ILogger<DoubleRatchetModel> logger,
        ISerializer serializer,
        uint maxSkip = 1000)
    {
        _logger = logger;
        _serializer = serializer;
        MaxSkip = maxSkip;
        DHs = dHs;
        DHr = dHr;
        RootKey = rootKey;
    }

    public static DoubleRatchetModel CreateSender(ECDiffieHellmanPublicKey otherPublicKey,
        byte[] secretKey,
        ILogger<DoubleRatchetModel> logger,
        ISerializer serializer)
    {
        var dHs = ECDiffieHellman.Create();
        var (rootKey, chainKey) = KDF_RK(secretKey, dHs.DeriveKeyMaterial(otherPublicKey), KDF_RK_Info);
        var model = new DoubleRatchetModel(dHs, otherPublicKey, rootKey,logger,serializer);
        model.CKs = chainKey;
        return model;
    }
    
    public static DoubleRatchetModel CreateReceiver(ECDiffieHellman dHs,
        byte[] secretKey,
        ILogger<DoubleRatchetModel> logger,
        ISerializer serializer)
    {
        return new DoubleRatchetModel(dHs, null, secretKey,logger, serializer);
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
    
    public (byte[] header, byte[] encrypted, byte[] headerSignature) RatchetEncrypt(byte[] plainText, byte[] associatedData)
    {
        var (nextCKs, mk) = KDF_CK(CKs);
        CKs = nextCKs;
        var header = new HeaderWrapper
        {
            Header = new HeaderWrapper.HeaderType
            {
                PublicKey = DHs.PublicKey.ToByteArray(),
                PreviousChainLength = PN,
                MessageNumber = Ns
            },
            AssociatedData = new HeaderWrapper.AssociatedDataType
            {
                Data = associatedData
            }
        };
        Ns++;
        var headerBytes = _serializer.Serialize(header);
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
    
    private byte[] Decrypt(byte[] messageKey, 
        byte[] cipherText, 
        HeaderWrapper headerWrapper,
        byte[] headerSignature,
        byte[] headerWrapperBytes)
    {
        var extracted = HKDF.Extract(HashAlgorithmName.SHA256, messageKey);
        var expanded = HKDF.Expand(HashAlgorithmName.SHA256, extracted, 80, EncryptInfo);
        
        var authenticationKey = expanded.Skip(32).Take(32).ToArray();
        var hmac = new HMACSHA256(authenticationKey);
        
        var actualSignature = hmac.ComputeHash(headerWrapperBytes.Concat(cipherText).ToArray());
        if (!ArrayEqualityComparer.Equals(actualSignature,headerSignature))
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

    public byte[]? RatchetDecrypt(byte[] headerBytes, byte[] cipherText, byte[] headerSignature)
    {
        var headerWrapper = _serializer.Deserialize(headerBytes);
        if (headerWrapper == null)
        {
            return null;
        }

        if (headerWrapper.Header == null || 
            headerWrapper.Header.PublicKey == null || 
            headerWrapper.Header.PreviousChainLength == null || 
            headerWrapper.Header.MessageNumber == null)
        {
            return null;
        }

        if (headerWrapper.AssociatedData == null || headerWrapper.AssociatedData.Data == null)
        {
            return null;
        }

        var currentSkippedKey = new MKSkippedKey
        {
            MessageNumber = headerWrapper.Header.MessageNumber.Value,
            MessagePublicKey = headerWrapper.Header.PublicKey
        };
        if (TrySkippedMessageKeys(currentSkippedKey, cipherText, headerWrapper,headerSignature,headerBytes, out var skippedBytes))
        {
            return skippedBytes;
        }
        
        if (DHr == null || !ArrayEqualityComparer.Equals(DHr.ToByteArray(),headerWrapper.Header.PublicKey))
        {
            SkipMessageKeys(headerWrapper.Header.PreviousChainLength.Value,currentSkippedKey);
            DHRatchet(headerWrapper.Header);
        }
        SkipMessageKeys(headerWrapper.Header.MessageNumber.Value,currentSkippedKey);
        var (nextCKr, mk) = KDF_CK(CKr);
        Nr++;
        CKr = nextCKr;
        var decrypted = Decrypt(mk, cipherText, headerWrapper,headerSignature,headerBytes);
        return decrypted;
    }

    private void DHRatchet(HeaderWrapper.HeaderType header)
    {
        PN = Ns;
        Ns = 0;
        Nr = 0;
        var publicKey = ECDiffieHellmanCngPublicKey.FromByteArray(header.PublicKey, CngKeyBlobFormat.EccPublicBlob);
        
        
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
        HeaderWrapper headerWrapper, 
        byte[] headerSignature, 
        byte[] headerBytes,
        [NotNullWhen(true)] out byte[]? decrypted)
    {
        if (!MKSkipped.Remove(currentSkippedKey, out var mk))
        {
            decrypted = null;
            return false;
        }
        decrypted = Decrypt(mk, cipherText, headerWrapper,headerSignature,headerBytes);
        return true;
    }
    
    public class MKSkippedKey : IEquatable<MKSkippedKey>
    {
        private static readonly ArrayEqualityComparer<byte> arrayComparer = new();
        public uint MessageNumber { get; init; }
        public byte[] MessagePublicKey { get; init; }
    
        public bool Equals(MKSkippedKey? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return MessageNumber == other.MessageNumber && arrayComparer.Equals(MessagePublicKey, other.MessagePublicKey);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(MessageNumber, arrayComparer.GetHashCode(MessagePublicKey));
        }

        public static bool operator ==(MKSkippedKey? left, MKSkippedKey? right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(MKSkippedKey? left, MKSkippedKey? right)
        {
            return !Equals(left, right);
        }
    }
}



