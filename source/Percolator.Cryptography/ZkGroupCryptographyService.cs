using Signal.Interop;

namespace Percolator.Cryptography;

/// <summary>
/// Implementation of ZK group cryptography using Signal.Interop native functions.
/// </summary>
public sealed class ZkGroupCryptographyService : IZkGroupCryptographyService
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
