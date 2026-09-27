using Percolator.Domain.Conversations.ValueObjects;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Domain.Tests.Common;

[TestFixture]
public class GuidIdTests
{
    [Test]
    public void PublicIdentityId_CryptographicRandom_GeneratesUniqueValidIds()
    {
        var id1 = PublicIdentityId.New();
        var id2 = PublicIdentityId.New();

        id1.IsValid.Should().BeTrue();
        id2.IsValid.Should().BeTrue();
        id1.Should().NotBe(id2);

        var defaultId = default(PublicIdentityId);
        defaultId.IsValid.Should().BeFalse();
    }

    [Test]
    public void EnsureValid_WhenDefault_ThrowsInvalidOperationException()
    {
        var defaultId = default(PublicIdentityId);
        var act = () => defaultId.EnsureValid();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot be uninitialized or empty*");
    }

    [Test]
    public void EnsureValid_WithParamName_WhenDefault_ThrowsArgumentException()
    {
        var defaultId = default(PublicIdentityId);
        var act = () => defaultId.EnsureValid("identityId");

        act.Should().Throw<ArgumentException>()
            .WithParameterName("identityId");
    }

    [Test]
    public void EnsureValid_WhenValid_DoesNotThrow()
    {
        var validId = PublicIdentityId.New();
        var act = () => validId.EnsureValid();

        act.Should().NotThrow();
    }

    [Test]
    public void FromGuid_CreatesValidIdentifier()
    {
        var guid = Guid.NewGuid();
        var id = PublicIdentityId.FromGuid(guid);

        id.Value.Should().Be(guid);
        id.IsValid.Should().BeTrue();
    }

    [Test]
    public void FromBytes_And_TryWriteBytes_RoundtripSuccessfully()
    {
        var original = BlindedRoutingToken.New();
        Span<byte> buffer = stackalloc byte[16];

        original.TryWriteBytes(buffer).Should().BeTrue();

        var restored = BlindedRoutingToken.FromBytes(buffer);
        restored.Should().Be(original);
        restored.Value.Should().Be(original.Value);
    }

    [Test]
    public void MessageId_SequentialTimeBased_EmbedsTimestampAndOrdersMonotonically()
    {
        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        var msg1 = MessageId.New(t1);
        var msg2 = MessageId.New(t2);

        msg1.CompareTo(msg2).Should().BeNegative();
        (msg1.Value < msg2.Value).Should().BeTrue();
    }

    [Test]
    public void Parse_And_ToString_RoundtripsSuccessfully()
    {
        var token = BlindedRoutingToken.New();
        var str = token.ToString();

        var parsed = BlindedRoutingToken.Parse(str, null);
        parsed.Should().Be(token);

        BlindedRoutingToken.TryParse(str, null, out var tryParsed).Should().BeTrue();
        tryParsed.Should().Be(token);
    }

    [Test]
    public void ExplicitCast_ToAndFromGuid_WorksCorrectly()
    {
        var rawGuid = Guid.NewGuid();
        var convId = (ConversationId)rawGuid;

        convId.Value.Should().Be(rawGuid);
        ((Guid)convId).Should().Be(rawGuid);
    }
}
