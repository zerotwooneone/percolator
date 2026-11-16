// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Service abstraction for generating QR code PNG image bytes from text payloads.
/// Decouples ViewModels from graphics rendering and external QR generation libraries.
/// </summary>
public interface IQrCodeGenerator
{
    /// <summary>
    /// Generates PNG image bytes representing the specified content as a QR code.
    /// </summary>
    /// <param name="content">The text content or URL to encode.</param>
    /// <returns>Raw PNG image byte array.</returns>
    byte[] GeneratePngBytes(string content);
}
