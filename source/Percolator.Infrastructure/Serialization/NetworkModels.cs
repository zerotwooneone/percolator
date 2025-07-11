using System;
using System.Collections.Generic;
using System.Net;

namespace Percolator.Infrastructure.Serialization;

public class PeerConnectionModel
{
    public Guid Id { get; set; }
    public byte[] DirectMessagePublicKey { get; set; } = Array.Empty<byte>();
    public List<GrpcEndPointModel> GrpcEndPoints { get; set; } = new();
    public List<TlsCertificateModel> TlsCertificates { get; set; } = new();
    public DateTimeOffset LastSeen { get; set; }
}

public class GrpcEndPointModel
{
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public class TlsCertificateModel
{
    public byte[] RawData { get; set; } = Array.Empty<byte>();
}
