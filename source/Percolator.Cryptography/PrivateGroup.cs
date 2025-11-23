using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Percolator.Cryptography;

public sealed class PrivateGroup
{
    public Guid GroupId { get; }
    public ulong SequenceNumber { get; private set; }
    private readonly HashSet<string> _members = new();
    private byte[]? _currentSenderKey;

    private PrivateGroup(Guid groupId, ulong sequence)
    {
        GroupId = groupId;
        SequenceNumber = sequence;
    }

    public static PrivateGroup CreateGroup()
    {
        var gid = Guid.NewGuid();
        return new PrivateGroup(gid, 0UL);
    }

    public byte[] CreateInvitePayload(ECDsa creatorSigningKey)
    {
        if (creatorSigningKey is null) throw new ArgumentNullException(nameof(creatorSigningKey));
        // Minimal payload: [groupId(16) | seq(8) | signature(len var, appended)]
        Span<byte> header = stackalloc byte[24];
        GroupId.TryWriteBytes(header);
        BinaryPrimitives.WriteUInt64BigEndian(header.Slice(16, 8), SequenceNumber);
        var headerBytes = header.ToArray();
        var signature = creatorSigningKey.SignData(headerBytes, HashAlgorithmName.SHA256);
        var payload = new byte[headerBytes.Length + signature.Length];
        Buffer.BlockCopy(headerBytes, 0, payload, 0, headerBytes.Length);
        Buffer.BlockCopy(signature, 0, payload, headerBytes.Length, signature.Length);
        return payload;
    }

    public static PrivateGroup AcceptInvite(byte[] payload, ECDsa creatorVerify)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        if (creatorVerify is null) throw new ArgumentNullException(nameof(creatorVerify));
        if (payload.Length < 24) throw new ArgumentException("invite payload too short", nameof(payload));
        var header = new byte[24];
        Buffer.BlockCopy(payload, 0, header, 0, 24);
        var signature = new byte[payload.Length - 24];
        Buffer.BlockCopy(payload, 24, signature, 0, signature.Length);
        if (!creatorVerify.VerifyData(header, signature, HashAlgorithmName.SHA256))
            throw new CryptographicException("invalid invite signature");
        var gidBytes = new byte[16];
        Buffer.BlockCopy(header, 0, gidBytes, 0, 16);
        var gid = new Guid(gidBytes);
        // seq is at header[16..24], we start at 0 for local state
        return new PrivateGroup(gid, 0UL);
    }

    public byte[] CreateAddMemberChange(byte[] memberIdentitySpki, ECDsa adminSigningKey)
    {
        if (memberIdentitySpki is null) throw new ArgumentNullException(nameof(memberIdentitySpki));
        if (adminSigningKey is null) throw new ArgumentNullException(nameof(adminSigningKey));
        // Encode: [gid(16) | seq(8) | memberLen(2) | memberSpki | sig]
        var nextSeq = SequenceNumber + 1UL;
        var memberLen = checked((ushort)memberIdentitySpki.Length);
        var header = new byte[16 + 8 + 2 + memberLen];
        GroupId.TryWriteBytes(header.AsSpan(0,16));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(16,8), nextSeq);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24,2), memberLen);
        Buffer.BlockCopy(memberIdentitySpki, 0, header, 26, memberLen);
        var sig = adminSigningKey.SignData(header, HashAlgorithmName.SHA256);
        var output = new byte[header.Length + sig.Length];
        Buffer.BlockCopy(header, 0, output, 0, header.Length);
        Buffer.BlockCopy(sig, 0, output, header.Length, sig.Length);
        SequenceNumber = nextSeq;
        return output;
    }

    public void ApplyAddMemberChange(byte[] changePayload, ECDsa adminVerify)
    {
        if (changePayload is null) throw new ArgumentNullException(nameof(changePayload));
        if (adminVerify is null) throw new ArgumentNullException(nameof(adminVerify));
        if (changePayload.Length < 26) throw new ArgumentException("change payload too short", nameof(changePayload));
        // Parse header
        var gidBytes = new byte[16];
        Buffer.BlockCopy(changePayload, 0, gidBytes, 0, 16);
        var gid = new Guid(gidBytes);
        if (gid != GroupId) throw new InvalidOperationException("change for different group");
        var seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(changePayload.AsSpan(16,8));
        var memberLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(changePayload.AsSpan(24,2));
        var headerLen = 26 + memberLen;
        if (changePayload.Length <= headerLen) throw new ArgumentException("change payload missing signature", nameof(changePayload));
        var header = new byte[headerLen];
        Buffer.BlockCopy(changePayload, 0, header, 0, headerLen);
        var sig = new byte[changePayload.Length - headerLen];
        Buffer.BlockCopy(changePayload, headerLen, sig, 0, sig.Length);
        if (!adminVerify.VerifyData(header, sig, HashAlgorithmName.SHA256)) throw new CryptographicException("invalid change signature");
        // Sequence rule: expect current+1
        if (seq != SequenceNumber + 1UL) throw new InvalidOperationException("unexpected change sequence");
        var memberSpki = new byte[memberLen];
        Buffer.BlockCopy(changePayload, 26, memberSpki, 0, memberLen);
        _members.Add(Convert.ToBase64String(memberSpki));
        SequenceNumber = seq;
    }

    public bool HasMember(byte[] memberIdentitySpki)
    {
        if (memberIdentitySpki is null) return false;
        return _members.Contains(Convert.ToBase64String(memberIdentitySpki));
    }

    public byte[] CreateRemoveMemberChange(byte[] memberIdentitySpki, ECDsa adminSigningKey)
    {
        if (memberIdentitySpki is null) throw new ArgumentNullException(nameof(memberIdentitySpki));
        if (adminSigningKey is null) throw new ArgumentNullException(nameof(adminSigningKey));
        // Same framing as add-member for simplicity in tests
        var nextSeq = SequenceNumber + 1UL;
        var memberLen = checked((ushort)memberIdentitySpki.Length);
        var header = new byte[16 + 8 + 2 + memberLen];
        GroupId.TryWriteBytes(header.AsSpan(0,16));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(16,8), nextSeq);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24,2), memberLen);
        Buffer.BlockCopy(memberIdentitySpki, 0, header, 26, memberLen);
        var sig = adminSigningKey.SignData(header, HashAlgorithmName.SHA256);
        var output = new byte[header.Length + sig.Length];
        Buffer.BlockCopy(header, 0, output, 0, header.Length);
        Buffer.BlockCopy(sig, 0, output, header.Length, sig.Length);
        SequenceNumber = nextSeq;
        return output;
    }

    public void ApplyRemoveMemberChange(byte[] changePayload, ECDsa adminVerify)
    {
        if (changePayload is null) throw new ArgumentNullException(nameof(changePayload));
        if (adminVerify is null) throw new ArgumentNullException(nameof(adminVerify));
        if (changePayload.Length < 26) throw new ArgumentException("change payload too short", nameof(changePayload));
        var gidBytes = new byte[16];
        Buffer.BlockCopy(changePayload, 0, gidBytes, 0, 16);
        var gid = new Guid(gidBytes);
        if (gid != GroupId) throw new InvalidOperationException("change for different group");
        var seq = BinaryPrimitives.ReadUInt64BigEndian(changePayload.AsSpan(16,8));
        var memberLen = BinaryPrimitives.ReadUInt16BigEndian(changePayload.AsSpan(24,2));
        var headerLen = 26 + memberLen;
        if (changePayload.Length <= headerLen) throw new ArgumentException("change payload missing signature", nameof(changePayload));
        var header = new byte[headerLen];
        Buffer.BlockCopy(changePayload, 0, header, 0, headerLen);
        var sig = new byte[changePayload.Length - headerLen];
        Buffer.BlockCopy(changePayload, headerLen, sig, 0, sig.Length);
        if (!adminVerify.VerifyData(header, sig, HashAlgorithmName.SHA256)) throw new CryptographicException("invalid change signature");
        if (seq != SequenceNumber + 1UL) throw new InvalidOperationException("unexpected change sequence");
        var memberSpki = new byte[memberLen];
        Buffer.BlockCopy(changePayload, 26, memberSpki, 0, memberLen);
        _members.Remove(Convert.ToBase64String(memberSpki));
        SequenceNumber = seq;
    }

    public byte[] CreateSenderKeyRotationPayload(byte[] newSenderKey, ECDsa adminSigningKey)
    {
        if (newSenderKey is null) throw new ArgumentNullException(nameof(newSenderKey));
        if (adminSigningKey is null) throw new ArgumentNullException(nameof(adminSigningKey));
        var nextSeq = SequenceNumber + 1UL;
        var keyLen = checked((ushort)newSenderKey.Length);
        var header = new byte[16 + 8 + 2 + keyLen];
        GroupId.TryWriteBytes(header.AsSpan(0,16));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(16,8), nextSeq);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24,2), keyLen);
        Buffer.BlockCopy(newSenderKey, 0, header, 26, keyLen);
        var sig = adminSigningKey.SignData(header, HashAlgorithmName.SHA256);
        var output = new byte[header.Length + sig.Length];
        Buffer.BlockCopy(header, 0, output, 0, header.Length);
        Buffer.BlockCopy(sig, 0, output, header.Length, sig.Length);
        SequenceNumber = nextSeq;
        return output;
    }

    public void ApplySenderKeyRotationPayload(byte[] rotationPayload, ECDsa adminVerify)
    {
        if (rotationPayload is null) throw new ArgumentNullException(nameof(rotationPayload));
        if (adminVerify is null) throw new ArgumentNullException(nameof(adminVerify));
        if (rotationPayload.Length < 26) throw new ArgumentException("rotation payload too short", nameof(rotationPayload));
        var gidBytes = new byte[16];
        Buffer.BlockCopy(rotationPayload, 0, gidBytes, 0, 16);
        var gid = new Guid(gidBytes);
        if (gid != GroupId) throw new InvalidOperationException("rotation for different group");
        var seq = BinaryPrimitives.ReadUInt64BigEndian(rotationPayload.AsSpan(16,8));
        var keyLen = BinaryPrimitives.ReadUInt16BigEndian(rotationPayload.AsSpan(24,2));
        var headerLen = 26 + keyLen;
        if (rotationPayload.Length <= headerLen) throw new ArgumentException("rotation payload missing signature", nameof(rotationPayload));
        var header = new byte[headerLen];
        Buffer.BlockCopy(rotationPayload, 0, header, 0, headerLen);
        var sig = new byte[rotationPayload.Length - headerLen];
        Buffer.BlockCopy(rotationPayload, headerLen, sig, 0, sig.Length);
        if (!adminVerify.VerifyData(header, sig, HashAlgorithmName.SHA256)) throw new CryptographicException("invalid rotation signature");
        if (seq != SequenceNumber + 1UL) throw new InvalidOperationException("unexpected rotation sequence");
        var key = new byte[keyLen];
        Buffer.BlockCopy(rotationPayload, 26, key, 0, keyLen);
        _currentSenderKey = key;
        SequenceNumber = seq;
    }

    public byte[] GetCurrentSenderKey()
    {
        if (_currentSenderKey is null) throw new InvalidOperationException("sender key not set");
        return _currentSenderKey;
    }
}
