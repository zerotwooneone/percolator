namespace Percolator.Apps.Discovery.Ingress;

/// <summary>
/// 1-byte wire opcode prefixing inbound and outbound Discovery application payloads (AppId = 0x02).
/// </summary>
public enum DiscoveryOpcode : byte
{
    RegisterVisibility = 0x01,
    NearbyQuery = 0x02,
    NearbyResponse = 0x03,
    RendezvousPublish = 0x04,
    RendezvousQuery = 0x05,
    RendezvousResponse = 0x06
}
