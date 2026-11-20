namespace Percolator.Infrastructure.Tests.Plugins.BlobStorage;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Infrastructure.Plugins.BlobStorage;

[TestFixture]
public sealed class StreamingAesGcmCryptoServiceTests
{
    private StreamingAesGcmCryptoService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new StreamingAesGcmCryptoService();
    }

    [Test]
    public async Task ExifScrubbing_StripsGpsAndDeviceMetadata()
    {
        // Construct a synthetic JPEG with an APP1 (EXIF) segment and an image body
        using var jpegStream = new MemoryStream();
        byte[] soi = new byte[] { 0xFF, 0xD8 };
        byte[] app1Marker = new byte[] { 0xFF, 0xE1 };
        byte[] exifPayload = System.Text.Encoding.ASCII.GetBytes("Exif\0\0GPSInfoAndDeviceSerialDataHere");
        ushort app1Length = (ushort)(exifPayload.Length + 2);
        byte[] lenBytes = new byte[] { (byte)(app1Length >> 8), (byte)(app1Length & 0xFF) };
        byte[] imageBody = new byte[] { 0xFF, 0xDA, 0x00, 0x0C, 0x01, 0x02, 0x03, 0x04, 0xFF, 0xD9 };

        await jpegStream.WriteAsync(soi);
        await jpegStream.WriteAsync(app1Marker);
        await jpegStream.WriteAsync(lenBytes);
        await jpegStream.WriteAsync(exifPayload);
        await jpegStream.WriteAsync(imageBody);
        jpegStream.Position = 0;

        var packageResult = await _service.EncryptAndPackageAsync(jpegStream, "image/jpeg");

        packageResult.IsSuccess.Should().BeTrue();
        packageResult.Value.Should().NotBeNull();
        var package = packageResult.Value!;

        // Decrypt and assert that the EXIF segment was stripped
        var decryptResult = await _service.DecryptStreamAsync(
            package.CiphertextStream,
            package.EncryptionKey,
            package.BaseNonce,
            package.PlaintextSize);

        decryptResult.IsSuccess.Should().BeTrue();
        decryptResult.Value.Should().NotBeNull();

        using var decryptedMs = new MemoryStream();
        await decryptResult.Value!.CopyToAsync(decryptedMs);
        byte[] decryptedBytes = decryptedMs.ToArray();

        // The scrubbed JPEG must contain SOI and image body, but NOT the EXIF string
        System.Text.Encoding.ASCII.GetString(decryptedBytes).Should().NotContain("GPSInfoAndDeviceSerialDataHere");
        decryptedBytes[0].Should().Be(0xFF);
        decryptedBytes[1].Should().Be(0xD8);
    }

    [Test]
    public async Task ChunkedAead_SingleBitflip_FailsAuthentication()
    {
        byte[] rawPlaintext = new byte[70 * 1024]; // 70 KB spans 2 blocks (64 KB + 6 KB)
        RandomNumberGenerator.Fill(rawPlaintext);

        using var plainStream = new MemoryStream(rawPlaintext);
        var packageResult = await _service.EncryptAndPackageAsync(plainStream, "application/octet-stream");

        packageResult.IsSuccess.Should().BeTrue();
        var package = packageResult.Value!;

        // Read ciphertext to tamper with 1 bit in block 0
        using var cipherMs = new MemoryStream();
        await package.CiphertextStream.CopyToAsync(cipherMs);
        byte[] cipherBytes = cipherMs.ToArray();

        cipherBytes[100] ^= 0x01; // flip 1 bit

        using var tamperedStream = new MemoryStream(cipherBytes);
        var decryptResult = await _service.DecryptStreamAsync(
            tamperedStream,
            package.EncryptionKey,
            package.BaseNonce,
            package.PlaintextSize);

        decryptResult.IsSuccess.Should().BeFalse();
        decryptResult.Error.Should().NotBeNull();
        decryptResult.Error!.Code.Should().Be("AUTHENTICATION_FAILED");
    }

    [Test]
    public async Task ChunkedAead_Truncation_FailsFinalBlockAuthentication()
    {
        byte[] rawPlaintext = new byte[130 * 1024]; // 130 KB spans 3 blocks
        RandomNumberGenerator.Fill(rawPlaintext);

        using var plainStream = new MemoryStream(rawPlaintext);
        var packageResult = await _service.EncryptAndPackageAsync(plainStream, "application/octet-stream");

        packageResult.IsSuccess.Should().BeTrue();
        var package = packageResult.Value!;

        // Truncate the ciphertext by removing the last 20 bytes (stripping the final block tag)
        using var cipherMs = new MemoryStream();
        await package.CiphertextStream.CopyToAsync(cipherMs);
        byte[] cipherBytes = cipherMs.ToArray();

        byte[] truncatedBytes = new byte[cipherBytes.Length - 1000];
        Buffer.BlockCopy(cipherBytes, 0, truncatedBytes, 0, truncatedBytes.Length);

        using var truncatedStream = new MemoryStream(truncatedBytes);
        var decryptResult = await _service.DecryptStreamAsync(
            truncatedStream,
            package.EncryptionKey,
            package.BaseNonce,
            package.PlaintextSize);

        decryptResult.IsSuccess.Should().BeFalse();
        decryptResult.Error.Should().NotBeNull();
        decryptResult.Error!.Code.Should().Be("AUTHENTICATION_FAILED");
    }

    [Test]
    public async Task EncryptAndPackage_OversizedStream_FailsCeilingCheck()
    {
        // Virtual stream reporting > 100 MB length
        var oversizedStream = new FakeOversizedStream(101L * 1024 * 1024);

        var result = await _service.EncryptAndPackageAsync(oversizedStream, "application/octet-stream");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("BLOB_TOO_LARGE");
    }

    private sealed class FakeOversizedStream : MemoryStream
    {
        private readonly long _length;

        public FakeOversizedStream(long length)
        {
            _length = length;
        }

        public override long Length => _length;
        public override bool CanSeek => true;
    }
}
