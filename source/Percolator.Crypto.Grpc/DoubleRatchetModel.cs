using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Crypto.Grpc.Protos;

namespace Percolator.Crypto.Grpc;

public class DoubleRatchetModel
{
    private readonly Crypto.DoubleRatchetModel _model;

    private DoubleRatchetModel(Percolator.Crypto.DoubleRatchetModel model)
    {
        _model = model;
    }

    public static DoubleRatchetModel CreateReceiver(ECDiffieHellman dhs,
        byte[] rootKey, ILogger<Crypto.DoubleRatchetModel> logger, ISerializer serializer, uint maxSkip = 1000)
    {
        var model = Crypto.DoubleRatchetModel.CreateReceiver(dhs, rootKey, logger, serializer, maxSkip);
        return new DoubleRatchetModel(model);
    }
    public byte[] RatchetEncrypt(byte[] plainText, byte[] associatedData)
    {
        var (header, encrypted, headerSignature) = _model.RatchetEncrypt(plainText, associatedData);
        return new Payload
        {
            Data = ByteString.CopyFrom(encrypted),
            Header = ByteString.CopyFrom(header),
            HeaderSignature = ByteString.CopyFrom(headerSignature)
        }.ToByteArray();
    }
    
    public static byte[]? GetUnverifiedAssociatedData(
        ISerializer serializer,
        byte[] encrypted)
    {
        var payload = Payload.Parser.ParseFrom(encrypted);
        var bytes = Crypto.DoubleRatchetModel.GetUnverifiedAssociatedData(
            serializer,
            payload.Header.ToByteArray());
        return bytes;
    }

    public DecryptResult Decrypt(byte[] encrypted)
    {
        var payload = Payload.Parser.ParseFrom(encrypted);
        var tuple = _model.RatchetDecrypt(
            payload.Data.ToByteArray(),
            payload.Header.ToByteArray(),
            payload.HeaderSignature.ToByteArray());
        if (tuple == null )   
        {
            throw new Exception("Failed to decrypt");
        }
        return new DecryptResult
        {
            PlainText = tuple.Value.plainText,
            AssociatedData = tuple.Value.associatedData,
            MessageNumber = tuple.Value.messageNumber
        };
    }
}