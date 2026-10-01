using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Tests.Identities;

[TestFixture]
public sealed class ContactNicknameTests
{
    private static readonly PublicIdentityId FallbackId = PublicIdentityId.New();

    [TestCase("Alice", "Alice")]
    [TestCase("  Bob  ", "Bob")]
    [TestCase("Bob\u0000\u001f\r\nBuilder", "BobBuilder")]
    [TestCase("Eve\u200B\u202EAdmin", "EveAdmin")] // Strips zero-width space and RTL override
    [TestCase(null, null)] // Fallback to IdentityId string
    [TestCase("", null)]
    [TestCase("   \t\r\n   ", null)]
    [TestCase("\u0000\u0001\u0002", null)]
    public void Create_SanitizesAndAppliesFallback(string? raw, string? expected)
    {
        var result = ContactNickname.Create(raw, FallbackId);

        var expectedValue = expected ?? FallbackId.ToString();
        result.Value.Should().Be(expectedValue);
    }

    [Test]
    public void Create_ExceedingMaxLength_TruncatesTo64Characters()
    {
        var longInput = new string('A', 100);

        var result = ContactNickname.Create(longInput, FallbackId);

        result.Value.Length.Should().Be(ContactNickname.MaxLength);
        result.Value.Should().Be(new string('A', 64));
    }

    [TestCase("ValidName")]
    [TestCase("Alice in Wonderland")]
    public void TryCreate_WithValidInput_ReturnsSuccess(string valid)
    {
        var result = ContactNickname.TryCreate(valid);

        result.IsSuccess.Should().BeTrue();
        result.Value.Value.Should().Be(valid);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\u0000\u001f")]
    public void TryCreate_WithEmptyOrControlOnlyInput_ReturnsInvalidNicknameError(string? invalid)
    {
        var result = ContactNickname.TryCreate(invalid);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_NICKNAME");
    }

    [Test]
    public void TryCreate_ExceedingMaxLength_ReturnsNicknameTooLongError()
    {
        var longInput = new string('X', ContactNickname.MaxLength + 1);

        var result = ContactNickname.TryCreate(longInput);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("NICKNAME_TOO_LONG");
    }
}
