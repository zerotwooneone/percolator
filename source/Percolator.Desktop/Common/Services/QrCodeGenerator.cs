// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using QRCoder;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Cross-platform QR code generator implementation using QRCoder.PngByteQRCode.
/// Produces raw PNG bytes without GDI+ or System.Drawing dependencies.
/// </summary>
public sealed class QrCodeGenerator : IQrCodeGenerator
{
    public byte[] GeneratePngBytes(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<byte>();
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        using var qrCode = new PngByteQRCode(data);
        return qrCode.GetGraphic(20);
    }
}
