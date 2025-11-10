using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;
using NetPeerId = Percolator.Network.PeerId;

namespace Percolator.Infrastructure.Network;

public class SqlitePeerConnectionRepository : IPeerConnectionRepository
{
    private readonly PercolatorDbContext _context;

    public SqlitePeerConnectionRepository(PercolatorDbContext context)
    {
        _context = context;
    }

    public async Task<PeerConnection?> GetByIdAsync(PeerId peerId)
    {
        var dbo = await _context.PeerConnections
            .AsNoTracking()
            .Include(p => p.GrpcEndPoints)
            .Include(p => p.TlsCertificates)
            .FirstOrDefaultAsync(p => p.PeerId == peerId.Value);

        return dbo is null ? null : ToDomain(dbo);
    }

    public async Task SaveAsync(PeerConnection peerConnection)
    {
        // Ensure principal (PeerIdentity) exists for FK
        var principal = await _context.PeerIdentities.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PeerId == peerConnection.Id.Value);
        if (principal is null)
        {
            throw new InvalidOperationException($"PeerIdentity not found for PeerId={peerConnection.Id.Value}. Seed or create the identity before saving PeerConnection.");
        }

        var existing = await _context.PeerConnections
            .Include(p => p.GrpcEndPoints)
            .Include(p => p.TlsCertificates)
            .FirstOrDefaultAsync(p => p.PeerId == peerConnection.Id.Value);
        //todo: enforce limits on the number of endpoints and certificates
        if (existing is null)
        {
            var newDbo = new PeerConnectionDbo
            {
                PeerId = peerConnection.Id.Value,
                DirectMessagePublicKey = peerConnection.IdentitySigningKey?.Value,
                LastSeen = peerConnection.LastSeen,
                RelayPeerId = peerConnection.RelayPeerId?.Value,
                GrpcEndPoints = peerConnection.GrpcEndPoints
                    .Select(e => new GrpcEndPointDbo
                    {
                        PeerId = peerConnection.Id.Value,
                        Host = e.EndPoint.Host,
                        Port = e.EndPoint.Port,
                        LastSeen = e.LastSeen
                    }).ToList(),
                TlsCertificates = peerConnection.TlsCertificates
                    .Select(c => new TlsCertificateDbo
                    {
                        PeerId = peerConnection.Id.Value,
                        RawData = c.RawData,
                        RawDataHash = SHA256.HashData(c.RawData)
                    }).ToList()
            };

            _context.PeerConnections.Add(newDbo);
        }
        else
        {
            existing.DirectMessagePublicKey = peerConnection.IdentitySigningKey?.Value;
            existing.LastSeen = peerConnection.LastSeen;
            existing.RelayPeerId = peerConnection.RelayPeerId?.Value;

            // Replace children for simplicity
            _context.GrpcEndPoints.RemoveRange(existing.GrpcEndPoints);
            _context.TlsCertificates.RemoveRange(existing.TlsCertificates);

            existing.GrpcEndPoints = peerConnection.GrpcEndPoints
                .Select(e => new GrpcEndPointDbo
                {
                    PeerId = peerConnection.Id.Value,
                    Host = e.EndPoint.Host,
                    Port = e.EndPoint.Port,
                    LastSeen = e.LastSeen
                }).ToList();

            existing.TlsCertificates = peerConnection.TlsCertificates
                .Select(c => new TlsCertificateDbo
                {
                    PeerId = peerConnection.Id.Value,
                    RawData = c.RawData,
                    RawDataHash = SHA256.HashData(c.RawData)
                }).ToList();
        }

        await _context.SaveChangesAsync();
    }

    public async Task<PeerConnection?> GetByPublicKey(DirectMessagePublicKey directMessagePublicKey)
    {
        var dbo = await _context.PeerConnections
            .AsNoTracking()
            .Include(p => p.GrpcEndPoints)
            .Include(p => p.TlsCertificates)
            .FirstOrDefaultAsync(p => p.DirectMessagePublicKey != null && p.DirectMessagePublicKey == directMessagePublicKey.Value);

        return dbo is null ? null : ToDomain(dbo);
    }

    public async Task<PeerConnection?> GetByTlsCertificateAsync(TlsCertificate tlsCertificate)
    {
        var hash = SHA256.HashData(tlsCertificate.RawData);
        var certDbo = await _context.TlsCertificates
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.RawDataHash == hash);

        if (certDbo is null) return null;

        var dbo = await _context.PeerConnections
            .AsNoTracking()
            .Include(p => p.GrpcEndPoints)
            .Include(p => p.TlsCertificates)
            .FirstOrDefaultAsync(p => p.PeerId == certDbo.PeerId);

        return dbo is null ? null : ToDomain(dbo);
    }

    public async Task UpdateDirectMessagePublicKeyAsync(PeerId peerId, DirectMessagePublicKey publicKey)
    {
        var dbo = await _context.PeerConnections.FirstOrDefaultAsync(p => p.PeerId == peerId.Value);
        if (dbo is null)
        {
            // Create if not exists to keep behavior consistent with SaveAsync upsert
            dbo = new PeerConnectionDbo { PeerId = peerId.Value };
            _context.PeerConnections.Add(dbo);
        }

        dbo.DirectMessagePublicKey = publicKey.Value;
        await _context.SaveChangesAsync();
    }

    public async Task SetRelayAsync(PeerId target, PeerId relayPeerId)
    {
        var idTarget = target.Value;
        var dbo = await _context.PeerConnections.FirstOrDefaultAsync(p => p.PeerId == idTarget);
        if (dbo is null)
        {
            dbo = new PeerConnectionDbo
            {
                PeerId = idTarget,
                RelayPeerId = relayPeerId.Value,
                LastSeen = DateTimeOffset.UtcNow
            };
            _context.PeerConnections.Add(dbo);
        }
        else
        {
            dbo.RelayPeerId = relayPeerId.Value;
        }
        await _context.SaveChangesAsync();
    }

    public async Task<PeerId?> GetRelayAsync(PeerId target)
    {
        var idTarget = target.Value;
        var dbo = await _context.PeerConnections.AsNoTracking().FirstOrDefaultAsync(p => p.PeerId == idTarget);
        if (dbo?.RelayPeerId is null) return null;
        return new PeerId(dbo.RelayPeerId.Value);
    }

    private static PeerConnection ToDomain(PeerConnectionDbo dbo)
    {
        var netPeerId = new NetPeerId(dbo.PeerId);
        var dm = dbo.DirectMessagePublicKey is null ? null : new DirectMessagePublicKey(dbo.DirectMessagePublicKey);
        var endpoints = dbo.GrpcEndPoints
            .Select(e => new GrpcEndPoint(new DnsEndPoint(e.Host, e.Port), e.LastSeen))
            .ToList();
        var certs = dbo.TlsCertificates
            .Select(c => new TlsCertificate(c.RawData))
            .ToList();

        var relay = dbo.RelayPeerId is null ? (NetPeerId?)null : new NetPeerId(dbo.RelayPeerId.Value);
        return new PeerConnection(netPeerId, dm, endpoints, certs, dbo.LastSeen, relay);
    }
}
