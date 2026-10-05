namespace Percolator.PluginSdk;

/// <summary>
/// Lightweight descriptor carried inside E2EE application payloads (e.g. Chat ChatMessageDto).
/// Fits comfortably within the 64 KB Double Ratchet payload limit.
/// </summary>
public sealed record BlobReference
{
    public required BlobId Id { get; init; }
    public required byte[] CiphertextSha256 { get; init; }
    public required byte[] EncryptionKey { get; init; }
    public required byte[] BaseNonce { get; init; }
    public required long PlaintextSizeBytes { get; init; }
    public required long CiphertextSizeBytes { get; init; }
    public required string MimeType { get; init; }
    public required DateTimeOffset ExpiresAtUtc { get; init; }
    public required BlobStorageMode StorageMode { get; init; }
    public string? RelayEndpointUri { get; init; }
    public string? BlurHash { get; init; }
    public byte[]? InlineThumbnail { get; init; }
    public byte[]? Waveform { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? DurationSeconds { get; init; }

    /// <summary>
    /// Explicitly sanitizes key material from diagnostic logs.
    /// </summary>
    public override string ToString() =>
        $"BlobReference {{ Id = {Id}, StorageMode = {StorageMode}, MimeType = {MimeType}, " +
        $"PlaintextSize = {PlaintextSizeBytes}, CiphertextSize = {CiphertextSizeBytes}, " +
        $"ExpiresAt = {ExpiresAtUtc:u}, EncryptionKey = [REDACTED], BaseNonce = [REDACTED] }}";
}
