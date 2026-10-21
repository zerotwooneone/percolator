using Percolator.Apps.FileTransfer.Features.TransferNegotiation;

namespace Percolator.Apps.FileTransfer.Features.IngressControl;

public readonly record struct ValidationResult(bool IsValid, string? ErrorMessage)
{
    public static ValidationResult Success() => new(true, null);
    public static ValidationResult Failure(string error) => new(false, error);
}

public static class IngressTransferOfferValidator
{
    public const int MaxFilesPerBundle = 50_000;
    public const int MaxChunksPerBundle = 1_000_000;
    public const long MaxBundleSizeBytes = 16_000_000_000_000L; // 16 TB
    public const int MaxInlineManifestBytes = 49_152; // 48 KB (preserves headroom in 64 KB Double Ratchet envelope)
    public const long MaxRemoteManifestBlobBytes = 8_388_608L; // 8 MB

    public static ValidationResult ValidateOffer(FileTransferOfferPayload offer)
    {
        if (offer.Summary.TotalFiles.Count > MaxFilesPerBundle)
            return ValidationResult.Failure($"Offer file count ({offer.Summary.TotalFiles.Count}) exceeds maximum allowed ({MaxFilesPerBundle}).");
        if (offer.Summary.TotalChunks.Count > MaxChunksPerBundle)
            return ValidationResult.Failure($"Offer chunk count ({offer.Summary.TotalChunks.Count}) exceeds maximum allowed ({MaxChunksPerBundle}).");
        if (offer.Summary.TotalSize.Bytes > MaxBundleSizeBytes)
            return ValidationResult.Failure($"Offer bundle size ({offer.Summary.TotalSize.Bytes} bytes) exceeds maximum allowed ({MaxBundleSizeBytes} bytes).");
        if (offer.Manifest.Kind == ManifestDistributionKind.Inline)
        {
            if (offer.Manifest.InlineData == null || offer.Manifest.InlineData.Length > MaxInlineManifestBytes)
                return ValidationResult.Failure($"Inline manifest exceeds maximum allowed limit ({MaxInlineManifestBytes} bytes).");
        }
        else if (offer.Manifest.Kind == ManifestDistributionKind.RemoteBlob)
        {
            if (offer.Manifest.BlobDescriptor == null ||
                offer.Manifest.BlobDescriptor.PlaintextSizeBytes.Bytes <= 0 ||
                offer.Manifest.BlobDescriptor.PlaintextSizeBytes.Bytes > MaxRemoteManifestBlobBytes)
                return ValidationResult.Failure($"Remote manifest blob size ({offer.Manifest.BlobDescriptor?.PlaintextSizeBytes.Bytes ?? 0} bytes) must be between 1 and {MaxRemoteManifestBlobBytes} bytes.");
        }
        return ValidationResult.Success();
    }
}
