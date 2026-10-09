using Percolator.Application2.Profiles;

namespace Percolator.Application2.Tests.Profiles;

[TestFixture]
public sealed class InvitationLinkTests
{
    [Test]
    public void ToString_WithPort_IncludesPort()
    {
        var link = new InvitationLink("peer.percolator.net", 5222, "PubKey12345");
        link.ToString().Should().Be("percolator://peer.percolator.net:5222/PubKey12345");
    }

    [Test]
    public void ToString_WithoutPort_OmitsPort()
    {
        var link = new InvitationLink("peer.percolator.net", "PubKey12345");
        link.ToString().Should().Be("percolator://peer.percolator.net/PubKey12345");
    }

    [Test]
    public void Parse_WithPort_ParsesCorrectly()
    {
        var raw = "percolator://192.168.1.100:8080/MyIdentityKeyHex";
        var parsed = InvitationLink.Parse(raw);

        parsed.Host.Should().Be("192.168.1.100");
        parsed.Port.Should().Be(8080);
        parsed.PublicKey.Should().Be("MyIdentityKeyHex");
    }

    [Test]
    public void Parse_WithoutPort_ParsesCorrectlyWithNullPort()
    {
        var raw = "percolator://peer.percolator.net/MyIdentityKeyHex";
        var parsed = InvitationLink.Parse(raw);

        parsed.Host.Should().Be("peer.percolator.net");
        parsed.Port.Should().BeNull();
        parsed.PublicKey.Should().Be("MyIdentityKeyHex");
    }

    [TestCase("http://peer.percolator.net/Key", "Invalid scheme")]
    [TestCase("percolator:///Key", "Host is missing")]
    [TestCase("percolator://peer.percolator.net/", "Public key is missing")]
    [TestCase("percolator://peer.percolator.net", "Public key is missing")]
    [TestCase("", "Invitation link is empty")]
    public void TryParse_InvalidUri_ReturnsFalse(string raw, string expectedErrorSubstring)
    {
        var success = InvitationLink.TryParse(raw, out var result, out var errorMessage);

        success.Should().BeFalse();
        result.Should().BeNull();
        errorMessage.Should().Contain(expectedErrorSubstring);
    }

    [Test]
    public void Parse_InvalidUri_ThrowsFormatException()
    {
        var action = () => InvitationLink.Parse("not-a-valid-uri");
        action.Should().Throw<FormatException>();
    }

    [Test]
    public void Constructor_InvalidPort_ThrowsArgumentOutOfRangeException()
    {
        var action = () => new InvitationLink("host", 0, "pubkey");
        action.Should().Throw<ArgumentOutOfRangeException>();

        var action2 = () => new InvitationLink("host", 70000, "pubkey");
        action2.Should().Throw<ArgumentOutOfRangeException>();
    }
}
