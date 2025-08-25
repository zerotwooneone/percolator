using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Percolator.Infrastructure.Persistence;
using Percolator.Network;
using IdPeerId = Percolator.Identity.PeerId;
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
        var idPeerId = new IdPeerId(peerId.Value);
        var dbo = await _context.PeerConnections
            .AsNoTracking()
            .Include(p => p.GrpcEndPoints)
            .Include(p => p.TlsCertificates)
            .FirstOrDefaultAsync(p => p.PeerId == idPeerId);

        return dbo is null ? null : ToDomain(dbo);
    }

    public async Task SaveAsync(PeerConnection peerConnection)
    {
        var idPeerId = new IdPeerId(peerConnection.Id.Value);

        var existing = await _context.PeerConnections
            .Include(p => p.GrpcEndPoints)
            .Include(p => p.TlsCertificates)
            .FirstOrDefaultAsync(p => p.PeerId == idPeerId);
        //todo: enforce limits on the number of endpoints and certificates
        if (existing is null)
        {
            var newDbo = new PeerConnectionDbo
            {
                PeerId = idPeerId,
                DirectMessagePublicKey = peerConnection.IdentitySigningKey?.Value,
                LastSeen = peerConnection.LastSeen,
                GrpcEndPoints = peerConnection.GrpcEndPoints
                    .Select(e => new GrpcEndPointDbo
                    {
                        PeerId = idPeerId,
                        Host = e.EndPoint.Host,
                        Port = e.EndPoint.Port,
                        LastSeen = e.LastSeen
                    }).ToList(),
                TlsCertificates = peerConnection.TlsCertificates
                    .Select(c => new TlsCertificateDbo
                    {
                        PeerId = idPeerId,
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

            // Replace children for simplicity
            _context.GrpcEndPoints.RemoveRange(existing.GrpcEndPoints);
            _context.TlsCertificates.RemoveRange(existing.TlsCertificates);

            existing.GrpcEndPoints = peerConnection.GrpcEndPoints
                .Select(e => new GrpcEndPointDbo
                {
                    PeerId = idPeerId,
                    Host = e.EndPoint.Host,
                    Port = e.EndPoint.Port,
                    LastSeen = e.LastSeen
                }).ToList();

            existing.TlsCertificates = peerConnection.TlsCertificates
                .Select(c => new TlsCertificateDbo
                {
                    PeerId = idPeerId,
                    RawData = c.RawData,
                    RawDataHash = SHA256.HashData(c.RawData)
                }).ToList();
        }

        await _context.SaveChangesAsync();
    }

    public async Task<PeerConnection?> GetByDirectMessage(DirectMessagePublicKey directMessagePublicKey)
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
        var idPeerId = new IdPeerId(peerId.Value);
        var dbo = await _context.PeerConnections.FirstOrDefaultAsync(p => p.PeerId == idPeerId);
        if (dbo is null)
        {
            // Create if not exists to keep behavior consistent with SaveAsync upsert
            dbo = new PeerConnectionDbo { PeerId = idPeerId };
            _context.PeerConnections.Add(dbo);
        }

        dbo.DirectMessagePublicKey = publicKey.Value;
        await _context.SaveChangesAsync();
    }

    private static PeerConnection ToDomain(PeerConnectionDbo dbo)
    {
        var netPeerId = new NetPeerId(dbo.PeerId.Value);
        var dm = dbo.DirectMessagePublicKey is null ? null : new DirectMessagePublicKey(dbo.DirectMessagePublicKey);
        var endpoints = dbo.GrpcEndPoints
            .Select(e => new GrpcEndPoint(new DnsEndPoint(e.Host, e.Port), e.LastSeen))
            .ToList();
        var certs = dbo.TlsCertificates
            .Select(c => new TlsCertificate(c.RawData))
            .ToList();

        return new PeerConnection(netPeerId, dm, endpoints, certs, dbo.LastSeen);
    }
}
