using Google.Protobuf;
using Percolator.SourceGenerators;

namespace Percolator.Cryptography;

/// <summary>
/// Represents a serialized ratchet message used for secure communication.
/// Encapsulates the serialization and deserialization of ratchet messages
/// between domain objects and protobuf for wire transmission.
/// </summary>
[ByteArray(minLength: 1, maxLength: 1000000)]
public sealed partial record SessionRatchetMessage
{
    /// <summary>
    /// Creates a SessionRatchetMessage from domain components.
    /// </summary>
    public static SessionRatchetMessage Create(
        RatchetEphemeralKey ratchetKey,
        ulong counter,
        ulong previousChainLength,
        Ciphertext ciphertext)
    {
        var protoMessage = new Contracts.RatchetMessage
        {
            Version = 1,
            Header = new Contracts.RatchetHeader
            {
                RatchetKey = ByteString.CopyFrom(ratchetKey.Span),
                Counter = counter,
                PreviousChainLength = previousChainLength
            },
            Ciphertext = ByteString.CopyFrom(ciphertext.Span)
        };

        using var ms = new MemoryStream();
        protoMessage.WriteTo(ms);
        return SessionRatchetMessage.FromBytesOwned(ms.ToArray());
    }

    /// <summary>
    /// Extracts the header information from the serialized message.
    /// </summary>
    public (RatchetEphemeralKey PreKey, ulong Counter, ulong PreviousChainLength) GetHeader()
    {
        var protoMessage = Contracts.RatchetMessage.Parser.ParseFrom(Span);
        if (protoMessage.Header == null)
        {
            throw new InvalidOperationException("Invalid ratchet message: header is missing");
        }
        
        return (
            RatchetEphemeralKey.FromBytesOwned(protoMessage.Header.RatchetKey.ToByteArray()),
            protoMessage.Header.Counter,
            protoMessage.Header.PreviousChainLength
        );
    }

    /// <summary>
    /// Extracts the ciphertext from the serialized message.
    /// </summary>
    public Ciphertext GetCiphertext()
    {
        var protoMessage = Contracts.RatchetMessage.Parser.ParseFrom(Span);
        if (!protoMessage.HasCiphertext)
        {
            throw new InvalidOperationException("Invalid ratchet message: ciphertext is missing");
        }
        
        return Ciphertext.FromBytesOwned(protoMessage.Ciphertext.ToByteArray());
    }

    /// <summary>
    /// Gets the header associated data used for AEAD encryption/decryption.
    /// This follows the same stable format as the original RatchetHeader.ToAssociatedData().
    /// </summary>
    public byte[] GetHeaderAssociatedData()
    {
        var header = GetHeader();
        
        // It is critical that this serialization is stable and canonical.
        // The order and format must be identical for both sender and receiver.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var preKeyBytes = header.PreKey.ToArray();
        writer.Write(preKeyBytes);
        writer.Write(header.Counter);
        writer.Write(header.PreviousChainLength);
        return stream.ToArray();
    }
    
    /// <summary>
    /// Gets the associated data for AEAD encryption/decryption.
    /// </summary>
    public static byte[] GetAssociatedData((RatchetEphemeralKey PreKey, ulong Counter, ulong PreviousChainLength) header, byte[] additionalData)
    {
        // It is critical that this serialization is stable and canonical.
        // The order and format must be identical for both sender and receiver.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var preKeyBytes = header.PreKey.ToArray();
        writer.Write(preKeyBytes);
        writer.Write(header.Counter);
        writer.Write(header.PreviousChainLength);
        writer.Write(additionalData);
        return stream.ToArray();
    }
}
