using Percolator.Cryptography.Primitives;
using Google.Protobuf;

namespace Percolator.Cryptography;

/// <summary>
/// Represents a serialized ratchet message used for secure communication.
/// Encapsulates the serialization and deserialization of ratchet messages
/// between domain objects and protobuf for wire transmission.
/// </summary>
public record SessionRatchetMessage(byte[] Value) : ByteArrayRecord(Value)
{
    /// <summary>
    /// Creates a SessionRatchetMessage from domain components.
    /// </summary>
    public static SessionRatchetMessage Create(
        RatchetEphemeralKey ratchetKey,
        ulong counter,
        Ciphertext ciphertext)
    {
        var protoMessage = new Contracts.RatchetMessage
        {
            Version = 1,
            Header = new Contracts.RatchetHeader
            {
                RatchetKey = ByteString.CopyFrom(ratchetKey.Value),
                Counter = counter
            },
            Ciphertext = ByteString.CopyFrom(ciphertext.Value)
        };

        using var ms = new MemoryStream();
        protoMessage.WriteTo(ms);
        return new SessionRatchetMessage(ms.ToArray());
    }

    /// <summary>
    /// Extracts the header information from the serialized message.
    /// </summary>
    public (RatchetEphemeralKey RatchetKey, ulong Counter) GetHeader()
    {
        var protoMessage = Contracts.RatchetMessage.Parser.ParseFrom(Value);
        if (protoMessage.Header == null)
        {
            throw new InvalidOperationException("Invalid ratchet message: header is missing");
        }
        
        return (
            new RatchetEphemeralKey(protoMessage.Header.RatchetKey.ToByteArray()),
            protoMessage.Header.Counter
        );
    }

    /// <summary>
    /// Extracts the ciphertext from the serialized message.
    /// </summary>
    public Ciphertext GetCiphertext()
    {
        var protoMessage = Contracts.RatchetMessage.Parser.ParseFrom(Value);
        if (!protoMessage.HasCiphertext)
        {
            throw new InvalidOperationException("Invalid ratchet message: ciphertext is missing");
        }
        
        return new Ciphertext(protoMessage.Ciphertext.ToByteArray());
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
        writer.Write(header.RatchetKey.Value);
        writer.Write(header.Counter);
        return stream.ToArray();
    }
}
