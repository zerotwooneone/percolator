using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Domain.Identities.ValueObjects;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Features.SwarmCoordination;

public sealed class PieceAvailabilityIndex
{
    private readonly int _totalChunks;
    private readonly int[] _availability;
    private readonly Dictionary<PublicIdentityId, Bitfield> _peerBitfields = [];

    public PieceAvailabilityIndex(ChunkCount totalChunks)
    {
        if (totalChunks.Count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalChunks), "Total chunks must be positive.");
        }
        _totalChunks = totalChunks.Count;
        _availability = new int[_totalChunks];
    }

    public int GetAvailability(ChunkIndex chunkIndex)
    {
        return (chunkIndex.Value >= 0 && chunkIndex.Value < _totalChunks)
            ? _availability[chunkIndex.Value]
            : 0;
    }

    public void SetPeerBitfield(PublicIdentityId peerId, Bitfield newBitfield)
    {
        ArgumentNullException.ThrowIfNull(newBitfield);

        if (_peerBitfields.TryGetValue(peerId, out Bitfield? oldBitfield))
        {
            DecrementBits(oldBitfield);
        }

        IncrementBits(newBitfield);
        _peerBitfields[peerId] = newBitfield;
    }

    public void RecordPeerHave(PublicIdentityId peerId, ChunkIndex chunkIndex)
    {
        if (chunkIndex.Value < 0 || chunkIndex.Value >= _totalChunks)
        {
            return;
        }

        if (_peerBitfields.TryGetValue(peerId, out Bitfield? current))
        {
            if (!current.HasChunk(chunkIndex))
            {
                _availability[chunkIndex.Value]++;
                _peerBitfields[peerId] = current.WithChunk(chunkIndex);
            }
        }
        else
        {
            _availability[chunkIndex.Value]++;
            _peerBitfields[peerId] = Bitfield.Empty(ChunkCount.FromInt(_totalChunks)).WithChunk(chunkIndex);
        }
    }

    public void RemovePeer(PublicIdentityId peerId)
    {
        if (_peerBitfields.Remove(peerId, out Bitfield? oldBitfield))
        {
            DecrementBits(oldBitfield);
        }
    }

    public ChunkIndex? SelectRarestForPeer(
        PublicIdentityId peerId,
        Bitfield localBitfield,
        Bitfield requiredMask,
        Func<ChunkIndex, bool> isInFlight)
    {
        ArgumentNullException.ThrowIfNull(localBitfield);
        ArgumentNullException.ThrowIfNull(requiredMask);
        ArgumentNullException.ThrowIfNull(isInFlight);

        if (!_peerBitfields.TryGetValue(peerId, out Bitfield? peerBitfield) || peerBitfield.IsEmpty)
        {
            return null;
        }

        ReadOnlySpan<byte> reqSpan = requiredMask.AsSpan();
        ReadOnlySpan<byte> locSpan = localBitfield.AsSpan();
        ReadOnlySpan<byte> peerSpan = peerBitfield.AsSpan();

        int byteLen = Math.Min(reqSpan.Length, Math.Min(locSpan.Length, peerSpan.Length));
        int minAvail = int.MaxValue;
        ChunkIndex? rarestChunk = null;

        for (int byteIdx = 0; byteIdx < byteLen; byteIdx++)
        {
            byte candidateMask = (byte)(reqSpan[byteIdx] & ~locSpan[byteIdx] & peerSpan[byteIdx]);
            if (candidateMask == 0)
            {
                continue;
            }

            for (int bit = 0; bit < 8; bit++)
            {
                if ((candidateMask & (0x80 >> bit)) != 0)
                {
                    int chunkIdx = byteIdx * 8 + bit;
                    if (chunkIdx >= _totalChunks)
                    {
                        break;
                    }

                    ChunkIndex cIndex = ChunkIndex.FromInt(chunkIdx);
                    if (isInFlight(cIndex))
                    {
                        continue;
                    }

                    int avail = _availability[chunkIdx];
                    if (avail < minAvail)
                    {
                        minAvail = avail;
                        rarestChunk = cIndex;

                        if (minAvail == 1)
                        {
                            break;
                        }
                    }
                }
            }

            if (minAvail == 1 && rarestChunk.HasValue)
            {
                break;
            }
        }

        return rarestChunk;
    }

    private void DecrementBits(Bitfield bitfield)
    {
        ReadOnlySpan<byte> span = bitfield.AsSpan();
        for (int byteIdx = 0; byteIdx < span.Length; byteIdx++)
        {
            byte b = span[byteIdx];
            if (b == 0)
            {
                continue;
            }

            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (0x80 >> bit)) != 0)
                {
                    int chunkIdx = byteIdx * 8 + bit;
                    if (chunkIdx < _totalChunks && _availability[chunkIdx] > 0)
                    {
                        _availability[chunkIdx]--;
                    }
                }
            }
        }
    }

    private void IncrementBits(Bitfield bitfield)
    {
        ReadOnlySpan<byte> span = bitfield.AsSpan();
        for (int byteIdx = 0; byteIdx < span.Length; byteIdx++)
        {
            byte b = span[byteIdx];
            if (b == 0)
            {
                continue;
            }

            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (0x80 >> bit)) != 0)
                {
                    int chunkIdx = byteIdx * 8 + bit;
                    if (chunkIdx < _totalChunks)
                    {
                        _availability[chunkIdx]++;
                    }
                }
            }
        }
    }
}
