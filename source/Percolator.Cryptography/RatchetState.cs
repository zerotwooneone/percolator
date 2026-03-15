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
}
