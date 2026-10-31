namespace Percolator.Infrastructure.Tests.Transport.Primitives;

using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class CallbackEndpointValidatorTests
{
    private CallbackEndpointValidator _validator = null!;

    [SetUp]
    public void SetUp()
    {
        _validator = new CallbackEndpointValidator();
    }

    [Test]
    public async Task RejectsPrivateAndLoopbackIps_UnlessDevFlag()
    {
        // 1. Loopback addresses default rejection
        var r1 = await _validator.ValidateEndpointAsync("127.0.0.1", 5000);
        r1.IsFailure.Should().BeTrue();
        r1.Error!.Code.Should().Be("LOOPBACK_BLOCKED");

        var r2 = await _validator.ValidateEndpointAsync("::1", 5000);
        r2.IsFailure.Should().BeTrue();
        r2.Error!.Code.Should().Be("LOOPBACK_BLOCKED");

        // 2. Unspecified addresses
        var r3 = await _validator.ValidateEndpointAsync("0.0.0.0", 5000);
        r3.IsFailure.Should().BeTrue();
        r3.Error!.Code.Should().Be("UNSPECIFIED_ADDRESS");

        var r4 = await _validator.ValidateEndpointAsync("::", 5000);
        r4.IsFailure.Should().BeTrue();
        r4.Error!.Code.Should().Be("UNSPECIFIED_ADDRESS");

        // 3. RFC 1918 private subnets
        var r5 = await _validator.ValidateEndpointAsync("10.0.0.1", 5000);
        r5.IsFailure.Should().BeTrue();
        r5.Error!.Code.Should().Be("PRIVATE_NETWORK_BLOCKED");

        var r6 = await _validator.ValidateEndpointAsync("172.16.5.10", 5000);
        r6.IsFailure.Should().BeTrue();
        r6.Error!.Code.Should().Be("PRIVATE_NETWORK_BLOCKED");

        var r7 = await _validator.ValidateEndpointAsync("192.168.1.1", 5000);
        r7.IsFailure.Should().BeTrue();
        r7.Error!.Code.Should().Be("PRIVATE_NETWORK_BLOCKED");

        // 4. Link-local
        var r8 = await _validator.ValidateEndpointAsync("169.254.10.20", 5000);
        r8.IsFailure.Should().BeTrue();
        r8.Error!.Code.Should().Be("LINK_LOCAL_BLOCKED");

        // 5. IPv4-mapped IPv6 loopback
        var r9 = await _validator.ValidateEndpointAsync("::ffff:127.0.0.1", 5000);
        r9.IsFailure.Should().BeTrue();
        r9.Error!.Code.Should().Be("LOOPBACK_BLOCKED");

        // 6. IPv6 ULA
        var r10 = await _validator.ValidateEndpointAsync("fc00::1", 5000);
        r10.IsFailure.Should().BeTrue();
        r10.Error!.Code.Should().Be("PRIVATE_NETWORK_BLOCKED");

        // 7. Valid Public IP
        var rPublic = await _validator.ValidateEndpointAsync("93.184.216.34", 443);
        rPublic.IsSuccess.Should().BeTrue();
        rPublic.Value.Address.Should().Be(IPAddress.Parse("93.184.216.34"));
        rPublic.Value.Port.Should().Be(443);

        // 8. Loopback with AllowLoopback = true
        var loopbackOptions = new CallbackEndpointValidatorOptions { AllowLoopback = true };
        var rLoopbackAllowed = await _validator.ValidateEndpointAsync("127.0.0.1", 8080, loopbackOptions);
        rLoopbackAllowed.IsSuccess.Should().BeTrue();
        rLoopbackAllowed.Value.Address.Should().Be(IPAddress.Loopback);

        // 9. LAN with AllowLan = true
        var lanOptions = new CallbackEndpointValidatorOptions { AllowLan = true };
        var rLanAllowed = await _validator.ValidateEndpointAsync("192.168.1.100", 8080, lanOptions);
        rLanAllowed.IsSuccess.Should().BeTrue();
        rLanAllowed.Value.Address.Should().Be(IPAddress.Parse("192.168.1.100"));

        var rUlaAllowed = await _validator.ValidateEndpointAsync("fc00::1234", 8080, lanOptions);
        rUlaAllowed.IsSuccess.Should().BeTrue();
        rUlaAllowed.Value.Address.Should().Be(IPAddress.Parse("fc00::1234"));
    }
}
