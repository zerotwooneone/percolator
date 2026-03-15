using FluentAssertions;
using Microsoft.Extensions.Options;
using Percolator.Application.ReverseSignal;

namespace Percolator.ApplicationTests.ReverseSignal;

[TestFixture]
public sealed class CallbackEndpointValidatorTests
{
    [Test]
    public void Validate_Allows_DnsName_WhenPortValid()
    {
        var v = new CallbackEndpointValidator(Options.Create(new ReverseSignalOptions { AllowLan = false, AllowLoopback = false }));
        var result = v.Validate("example.com", 443);
        result.IsValid.Should().BeTrue();
        result.IsIpAddress.Should().BeFalse();
    }

    [Test]
    public void Validate_Rejects_LoopbackIp_WhenAllowLanFalse()
    {
        var v = new CallbackEndpointValidator(Options.Create(new ReverseSignalOptions { AllowLan = false, AllowLoopback = false }));
        var result = v.Validate("127.0.0.1", 443);
        result.IsValid.Should().BeFalse();
        result.IsIpAddress.Should().BeTrue();
        result.IsLanTarget.Should().BeTrue();
    }

    [Test]
    public void Validate_Rejects_LoopbackIp_WhenAllowLanTrueButAllowLoopbackFalse()
    {
        var v = new CallbackEndpointValidator(Options.Create(new ReverseSignalOptions { AllowLan = true, AllowLoopback = false }));
        var result = v.Validate("127.0.0.1", 443);
        result.IsValid.Should().BeFalse();
        result.IsIpAddress.Should().BeTrue();
        result.IsLanTarget.Should().BeTrue();
    }

    [Test]
    public void Validate_Allows_LoopbackIp_WhenAllowLanTrue()
    {
        var v = new CallbackEndpointValidator(Options.Create(new ReverseSignalOptions { AllowLan = true, AllowLoopback = true }));
        var result = v.Validate("127.0.0.1", 443);
        result.IsValid.Should().BeTrue();
        result.IsIpAddress.Should().BeTrue();
        result.IsLanTarget.Should().BeTrue();
    }

    [Test]
    public void Validate_Rejects_BadPort()
    {
        var v = new CallbackEndpointValidator(Options.Create(new ReverseSignalOptions { AllowLan = true, AllowLoopback = true }));
        v.Validate("example.com", 0).IsValid.Should().BeFalse();
        v.Validate("example.com", 70000).IsValid.Should().BeFalse();
    }
}
