using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Features.Media;
using Percolator.PluginSdk;

namespace Percolator.Apps.Chat.Tests.Features.Media;

[TestFixture]
public class ChatMediaServiceTests
{
    private ChatMediaService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _sut = new ChatMediaService();
    }

    [TestCase(0, true)]
    [TestCase(1024, true)]
    [TestCase(32 * 1024, true)]
    [TestCase(32 * 1024 + 1, false)]
    [TestCase(100 * 1024, false)]
    public void IsInlineEligible_Evaluates32KbThreshold(long byteSize, bool expectedResult)
    {
        _sut.IsInlineEligible(byteSize).Should().Be(expectedResult);
    }

    [TestCase("IMG_20261010_180512.jpg", "image/jpeg", null)]
    [TestCase("video_record.mp4", "video/mp4", null)]
    [TestCase("audio_memo.aac", "audio/aac", null)]
    [TestCase("../../../passwords.txt", "text/plain", "passwords.txt")]
    [TestCase("annual_report.pdf", "application/pdf", "annual_report.pdf")]
    public void SanitizeFileName_StripsMediaFilenamesAndSanitizesDocuments(string? inputFileName, string contentType, string? expectedFileName)
    {
        var sanitized = _sut.SanitizeFileName(inputFileName, contentType);
        sanitized.Should().Be(expectedFileName);
    }

    [Test]
    public void PrepareInlineAttachment_WhenOverSizeLimit_ReturnsAttachmentTooLargeError()
    {
        var overLimitBytes = new byte[50 * 1024 * 1024 + 1];
        var result = _sut.PrepareInlineAttachment(
            new BlobId("test"),
            "doc.pdf",
            "application/pdf",
            overLimitBytes);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("ATTACHMENT_TOO_LARGE");
    }

    [Test]
    public void PrepareInlineAttachment_WhenWithinInlineLimit_EmbedsInlineBytes()
    {
        var data = new byte[1024];
        var result = _sut.PrepareInlineAttachment(
            new BlobId("test"),
            "photo.jpg",
            "image/jpeg",
            data);

        result.IsSuccess.Should().BeTrue();
        var attachment = result.Value;
        attachment.InlineBytes.Should().NotBeNull();
        attachment.FileName.Should().BeNull(); // sanitized image filename stripped
        attachment.ByteSize.Should().Be(1024);
    }
}
