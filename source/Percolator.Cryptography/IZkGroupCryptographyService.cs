namespace Percolator.Cryptography;

/// <summary>
/// Service for verifying Zero-Knowledge group presentations.
/// </summary>
public interface IZkGroupCryptographyService
{
    /// <summary>
    /// Verifies a ZK presentation against the server's secret parameters and the group's public parameters.
    /// </summary>
    /// <param name="presentation">The ZK presentation bytes.</param>
    /// <param name="serverSecretSeed">The 32-byte randomness seed for the server's secret parameters.</param>
    /// <param name="groupPublic">The group's public parameters.</param>
    /// <param name="redemptionTime">The redemption time in epoch seconds.</param>
    /// <returns>True if the presentation is valid, false otherwise.</returns>
    bool VerifyGroupPresentation(
        ZkPresentationBytes presentation,
        ZkServerSecretParamsSeedBytes serverSecretSeed,
        ZkGroupPublicParamsBytes groupPublic,
        ulong redemptionTime);
}
