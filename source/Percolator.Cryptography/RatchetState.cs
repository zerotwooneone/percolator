using System;
using System.Text.Json;

namespace Percolator.Cryptography;

public class RatchetState
{
    public RootKey RootKey { get; }
    public ChainKey? SendingChainKey { get; }
    public ulong SendingCounter { get; }
    public ChainKey? ReceivingChainKey { get; }
    public ulong ReceivingCounter { get; }
    public ulong PreviousChainLength { get; }
    public RatchetEphemeralKey? RemoteRatchetKey { get; }
    public PrivateEphemeralKey? DhRatchetPrivateKey { get; }
    public int SkippedKeyLimit { get; }

    public RatchetState(
        RootKey rootKey,
        ChainKey? sendingChainKey,
        ulong sendingCounter,
        ChainKey? receivingChainKey,
        ulong receivingCounter,
        ulong previousChainLength,
        RatchetEphemeralKey? remoteRatchetKey,
        PrivateEphemeralKey? dhRatchetPrivateKey,
        int skippedKeyLimit)
    {
        RootKey = rootKey ?? throw new ArgumentNullException(nameof(rootKey));
        SendingChainKey = sendingChainKey;
        SendingCounter = sendingCounter;
        ReceivingChainKey = receivingChainKey;
        ReceivingCounter = receivingCounter;
        PreviousChainLength = previousChainLength;
        RemoteRatchetKey = remoteRatchetKey;
        DhRatchetPrivateKey = dhRatchetPrivateKey;
        if (skippedKeyLimit <= 0) throw new ArgumentOutOfRangeException(nameof(skippedKeyLimit));
        SkippedKeyLimit = skippedKeyLimit;
    }

    public byte[] ToSnapshotBytes()
    {
        var snapshot = new Snapshot
        {
            RootKey = RootKey.Value,
            SendingChainKey = SendingChainKey?.Value,
            SendingCounter = SendingCounter,
            ReceivingChainKey = ReceivingChainKey?.Value,
            ReceivingCounter = ReceivingCounter,
            PreviousChainLength = PreviousChainLength,
            RemoteRatchetKey = RemoteRatchetKey?.Value,
            DhRatchetPrivateKey = DhRatchetPrivateKey?.Value,
            SkippedKeyLimit = SkippedKeyLimit
        };
        return JsonSerializer.SerializeToUtf8Bytes(snapshot);
    }

    public static RatchetState FromSnapshotBytes(byte[] bytes)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(bytes)!;
        var root = new RootKey(snapshot.RootKey!);
        ChainKey? send = snapshot.SendingChainKey is null ? null : new ChainKey(snapshot.SendingChainKey);
        ChainKey? recv = snapshot.ReceivingChainKey is null ? null : new ChainKey(snapshot.ReceivingChainKey);
        RatchetEphemeralKey? remote = snapshot.RemoteRatchetKey is null ? null : new RatchetEphemeralKey(snapshot.RemoteRatchetKey);
        PrivateEphemeralKey? priv = snapshot.DhRatchetPrivateKey is null ? null : new PrivateEphemeralKey(snapshot.DhRatchetPrivateKey);
        return new RatchetState(
            root,
            send,
            snapshot.SendingCounter,
            recv,
            snapshot.ReceivingCounter,
            snapshot.PreviousChainLength,
            remote,
            priv,
            snapshot.SkippedKeyLimit);
    }

    private sealed class Snapshot
    {
        public byte[]? RootKey { get; set; }
        public byte[]? SendingChainKey { get; set; }
        public ulong SendingCounter { get; set; }
        public byte[]? ReceivingChainKey { get; set; }
        public ulong ReceivingCounter { get; set; }
        public ulong PreviousChainLength { get; set; }
        public byte[]? RemoteRatchetKey { get; set; }
        public byte[]? DhRatchetPrivateKey { get; set; }
        public int SkippedKeyLimit { get; set; }
    }
}
