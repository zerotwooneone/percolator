using Percolator.Application; // This namespace will be created next

namespace Percolator.ApplicationTests;

[TestFixture]
public class InvitationLinkTests
{
    [Test]
    public void Parse_WithValidLink_ShouldReturnCorrectComponents()
    {
        // Arrange
        const string host = "testhost";
        const int port = 1234;
        var publicKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("some-fake-key-data"));
        var link = $"percolator://{host}:{port}/{publicKey}";

        // Act
        var result = InvitationLink.Parse(link);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(result.Host, Is.EqualTo(host));
            Assert.That(result.Port, Is.EqualTo(port));
            Assert.That(result.PublicKey, Is.EqualTo(publicKey));
        });
    }

    [Test]
    public void ToString_FromComponents_ShouldCreateCorrectLink()
    {
        // Arrange
        const string host = "testhost";
        const int port = 1234;
        var publicKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("some-fake-key-data"));
        var expectedLink = $"percolator://{host}:{port}/{publicKey}";
        var invitationLink = new InvitationLink(host, port, publicKey);

        // Act
        var result = invitationLink.ToString();

        // Assert
        Assert.That(result, Is.EqualTo(expectedLink));
    }

    [TestCase("http://testhost:1234/key", "Invalid scheme 'http'. Expected 'percolator'.")]
    [TestCase("percolator://testhost/key", "Port is missing from the invitation link.")]
    [TestCase("percolator://testhost:1234", "Public key is missing from the invitation link.")]
    [TestCase("percolator://testhost:1234/", "Public key is missing from the invitation link.")]
    [TestCase("not-a-uri", "Invitation link is not a valid URI.")]
    public void Parse_WithInvalidLink_ShouldThrowFormatException(string link, string expectedMessage)
    {
        // Act & Assert
        var ex = Assert.Throws<FormatException>(() => InvitationLink.Parse(link));
        Assert.That(ex.Message, Is.EqualTo(expectedMessage));
    }
}
