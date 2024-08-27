using Google.Protobuf;
using Percolator.Crypto.Grpc.Protos;

namespace Percolator.Crypto.Grpc;

public class GrpcSerializer: ISerializer
{
    public byte[] Serialize(HeaderWrapper header)
    {
        var wrapper = new GrpcHeaderWrapper
        {
            Header = new GrpcHeaderWrapper.Types.Header
            {
                PublicKey = header.Header == null ? ByteString.Empty : ByteString.CopyFrom(header.Header.PublicKey),
            },

            AssociatedData = new GrpcHeaderWrapper.Types.AssociatedData()
        };

        if (header.Header != null)
        {
            if (header.Header.PreviousChainLength.HasValue)
            {
                wrapper.Header.PreviousChainLength = header.Header.PreviousChainLength.Value;
            }

            if (header.Header.MessageNumber.HasValue)
            {
                wrapper.Header.MessageNumber = header.Header.MessageNumber.Value;
            }
        }

        if (header.AssociatedData != null && header.AssociatedData.Data != null)
        {
            wrapper.AssociatedData.Data = ByteString.CopyFrom(header.AssociatedData.Data);
        }
        
        return wrapper.ToByteArray();
    }

    public HeaderWrapper? Deserialize(byte[] bytes)
    {
        var wrapper = GrpcHeaderWrapper.Parser.ParseFrom(bytes);
        
        return new HeaderWrapper
        {
            Header = new HeaderWrapper.HeaderType
            {
                PublicKey = wrapper.Header.PublicKey.ToByteArray(),
                PreviousChainLength = wrapper.Header.PreviousChainLength,
                MessageNumber = wrapper.Header.MessageNumber
            },
            AssociatedData = new HeaderWrapper.AssociatedDataType
            {
                Data = wrapper.AssociatedData.Data.ToByteArray()
            }
        };
    }
}

public class DecryptResult
{
    public byte[] PlainText { get; set; }
    public byte[] AssociatedData { get; set; }
    public uint MessageNumber { get; set; }
}