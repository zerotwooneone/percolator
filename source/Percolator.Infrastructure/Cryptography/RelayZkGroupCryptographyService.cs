using Signal.Interop;
using Percolator.Cryptography;

namespace Percolator.Infrastructure.Cryptography;

/// <summary>
/// Infrastructure implementation of IZkGroupCryptographyService using Signal.Interop native functions.
/// This service handles all native FFI integration and SafeHandle management for Relay ZK operations.
/// </summary>
public sealed class RelayZkGroupCryptographyService : IZkGroupCryptographyService
{
    public bool VerifyGroupPresentation(
        ZkPresentationBytes presentation,
        ZkServerSecretParamsSeedBytes serverSecretSeed,
        ZkGroupPublicParamsBytes groupPublic,
        ulong redemptionTime)
    {
        // CRITICAL: Must be called inside nested using blocks for SafeHandle management
        using var presentationHandle = SignalCrypto.DeserializeAuthCredentialWithPniPresentation(presentation.Value.Span);
        using var groupPublicHandle = SignalCrypto.DeserializeGroupPublicParams(groupPublic.Value.Span);
        using var serverSecretHandle = SignalCrypto.GenerateServerSecretParams(serverSecretSeed.Value.Span);

        SignalCrypto.VerifyAuthCredentialWithPniPresentation(
            presentationHandle,
            serverSecretHandle,
            groupPublicHandle,
            redemptionTime);

        return true;
    }
}
