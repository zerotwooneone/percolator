namespace Percolator.Infrastructure.Plugins.FileTransfer.WebRtc;

using System;
using System.Security.Cryptography;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Domain.Common;

/// <summary>
/// Strictly validates WebRTC SDP descriptions before native libdatachannel ingestion.
/// Enforces data-channel-only signaling (m=application only; prohibits m=audio, m=video,
/// m=text, m=image) and constant-time DTLS SHA-256 certificate fingerprint verification
/// against E2E-pinned peer fingerprints.
/// </summary>
public static class SdpSecurityValidator
{
    public const int MaxSdpLengthChars = 65_536; // 64 KB

    private static readonly string[] ProhibitedMediaPrefixes =
    [
        "m=audio",
        "m=video",
        "m=text",
        "m=image"
    ];

    public static DomainResult<CertSha256Fingerprint> ValidateAndExtractFingerprint(
        string? sdp,
        CertSha256Fingerprint? expectedRemoteFingerprint = null)
    {
        if (string.IsNullOrWhiteSpace(sdp))
        {
            return DomainResult<CertSha256Fingerprint>.Failure(
                new DomainError(TransferTransportErrorCodes.ProhibitedSdpMedia, "SDP payload cannot be empty."));
        }

        if (sdp.Length > MaxSdpLengthChars)
        {
            return DomainResult<CertSha256Fingerprint>.Failure(
                new DomainError(
                    TransferTransportErrorCodes.ProhibitedSdpMedia,
                    $"SDP length {sdp.Length} exceeds maximum allowed {MaxSdpLengthChars} characters."));
        }

        bool hasApplicationMedia = false;
        CertSha256Fingerprint? extractedFingerprint = null;

        ReadOnlySpan<char> remaining = sdp.AsSpan();
        while (!remaining.IsEmpty)
        {
            int lineEnd = remaining.IndexOf('\n');
            ReadOnlySpan<char> line;
            if (lineEnd < 0)
            {
                line = remaining;
                remaining = ReadOnlySpan<char>.Empty;
            }
            else
            {
                line = remaining.Slice(0, lineEnd);
                remaining = remaining.Slice(lineEnd + 1);
            }

            if (!line.IsEmpty && line[^1] == '\r')
            {
                line = line.Slice(0, line.Length - 1);
            }

            line = line.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            if (line.StartsWith("m=", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string prohibited in ProhibitedMediaPrefixes)
                {
                    if (line.StartsWith(prohibited, StringComparison.OrdinalIgnoreCase) &&
                        (line.Length == prohibited.Length || line[prohibited.Length] == ' '))
                    {
                        return DomainResult<CertSha256Fingerprint>.Failure(
                            new DomainError(
                                TransferTransportErrorCodes.ProhibitedSdpMedia,
                                $"SDP contains prohibited media line '{prohibited}'. Only m=application data channels are permitted."));
                    }
                }

                if (line.StartsWith("m=application ", StringComparison.OrdinalIgnoreCase))
                {
                    hasApplicationMedia = true;
                }
                else
                {
                    return DomainResult<CertSha256Fingerprint>.Failure(
                        new DomainError(
                            TransferTransportErrorCodes.ProhibitedSdpMedia,
                            "SDP contains an unsupported media section. Only m=application is permitted."));
                }
            }
            else if (line.StartsWith("a=fingerprint:", StringComparison.OrdinalIgnoreCase))
            {
                ReadOnlySpan<char> rest = line.Slice("a=fingerprint:".Length).Trim();
                if (!rest.StartsWith("sha-256 ", StringComparison.OrdinalIgnoreCase))
                {
                    return DomainResult<CertSha256Fingerprint>.Failure(
                        new DomainError(
                            TransferTransportErrorCodes.DtlsFingerprintMismatch,
                            "SDP a=fingerprint attribute must use sha-256."));
                }

                ReadOnlySpan<char> hex = rest.Slice("sha-256 ".Length).Trim();
                if (!CertSha256Fingerprint.TryParseColonHex(hex, out CertSha256Fingerprint? parsed) || parsed == null)
                {
                    return DomainResult<CertSha256Fingerprint>.Failure(
                        new DomainError(
                            TransferTransportErrorCodes.DtlsFingerprintMismatch,
                            "SDP a=fingerprint:sha-256 value is malformed."));
                }

                if (extractedFingerprint != null &&
                    !CryptographicOperations.FixedTimeEquals(extractedFingerprint.Span, parsed.Span))
                {
                    return DomainResult<CertSha256Fingerprint>.Failure(
                        new DomainError(
                            TransferTransportErrorCodes.DtlsFingerprintMismatch,
                            "SDP contains conflicting a=fingerprint:sha-256 attributes."));
                }

                extractedFingerprint = parsed;
            }
        }

        if (!hasApplicationMedia)
        {
            return DomainResult<CertSha256Fingerprint>.Failure(
                new DomainError(
                    TransferTransportErrorCodes.ProhibitedSdpMedia,
                    "SDP is missing required m=application data channel section."));
        }

        if (extractedFingerprint == null)
        {
            return DomainResult<CertSha256Fingerprint>.Failure(
                new DomainError(
                    TransferTransportErrorCodes.DtlsFingerprintMismatch,
                    "SDP is missing required a=fingerprint:sha-256 DTLS certificate fingerprint."));
        }

        if (expectedRemoteFingerprint != null &&
            !CryptographicOperations.FixedTimeEquals(extractedFingerprint.Span, expectedRemoteFingerprint.Span))
        {
            return DomainResult<CertSha256Fingerprint>.Failure(
                new DomainError(
                    TransferTransportErrorCodes.DtlsFingerprintMismatch,
                    "SDP DTLS SHA-256 fingerprint does not match the E2E-pinned remote peer fingerprint."));
        }

        return DomainResult<CertSha256Fingerprint>.Success(extractedFingerprint);
    }

    public static CertSha256Fingerprint ValidateOrThrow(
        string? sdp,
        CertSha256Fingerprint? expectedRemoteFingerprint = null)
    {
        DomainResult<CertSha256Fingerprint> result = ValidateAndExtractFingerprint(sdp, expectedRemoteFingerprint);
        if (result.IsFailure)
        {
            throw new TransferTransportException(result.Error.Code, result.Error.Description);
        }

        return result.Value;
    }
}
