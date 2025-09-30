using System;
using System.Threading.Tasks;
using NUnit.Framework;
using FluentAssertions;
using Percolator.Application.Handshake;
using Percolator.Network;

namespace Percolator.ApplicationTests.Handshake;

[TestFixture]
public class ResponderSessionDescriptorServiceTests
{
    [Test]
    public async Task Create_Then_Verify_Succeeds()
    {
        var svc = new ResponderSessionDescriptorService();
        var ephSpki = new byte[] { 0x30, 0x40 };
        var sid = Guid.NewGuid().ToString();

        var hello = await svc.CreateAsync(ephSpki, sid);
        hello.HasResponderEphemeralKey.Should().BeTrue();
        hello.HasDirectSessionId.Should().BeTrue();

        var parsed = await svc.VerifyAndParseAsync(hello);
        parsed.HasValue.Should().BeTrue();
        var (parsedEph, parsedSid) = parsed!.Value;
        parsedEph.Should().Equal(ephSpki);
        parsedSid.Should().Be(sid);
    }
}
