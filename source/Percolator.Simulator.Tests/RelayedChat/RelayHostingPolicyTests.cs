using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;

namespace Percolator.Simulator.Tests.RelayedChat;

[TestFixture]
public sealed class RelayHostingPolicyTests
{
    [Test]
    public void RelayHostingPolicyHolder_HotReloadsPolicyImmediatelyInMemory()
    {
        var initialPolicy = new RelayHostingPolicy(
            registrationStrategy: RelayRegistrationStrategy.ApprovalRequired,
            autoApproveLocalhost: false,
            enabledFeatures: RelayFeatureFlags.PreKeys | RelayFeatureFlags.Blobs,
            maxMailboxes: 100,
            maxMailboxBytes: 1024 * 1024,
            envelopeTtl: TimeSpan.FromHours(24));

        var holder = new RelayHostingPolicyHolder(initialPolicy);

        holder.GetPolicy().RegistrationStrategy.Should().Be(RelayRegistrationStrategy.ApprovalRequired);
        holder.GetPolicy().AutoApproveLocalhost.Should().BeFalse();
        holder.GetPolicy().IsFeatureEnabled(RelayFeatureFlags.PreKeys).Should().BeTrue();
        holder.GetPolicy().IsFeatureEnabled(RelayFeatureFlags.Profiles).Should().BeFalse();
        holder.GetPolicy().MaxMailboxes.Should().Be(100);

        // Mutate policy live
        var updatedPolicy = new RelayHostingPolicy(
            registrationStrategy: RelayRegistrationStrategy.Open,
            autoApproveLocalhost: true,
            enabledFeatures: RelayFeatureFlags.PreKeys | RelayFeatureFlags.Profiles | RelayFeatureFlags.Groups,
            maxMailboxes: 500,
            maxMailboxBytes: 5 * 1024 * 1024,
            envelopeTtl: TimeSpan.FromDays(7));

        holder.UpdatePolicy(updatedPolicy);

        var current = holder.GetPolicy();
        current.RegistrationStrategy.Should().Be(RelayRegistrationStrategy.Open);
        current.AutoApproveLocalhost.Should().BeTrue();
        current.IsFeatureEnabled(RelayFeatureFlags.Profiles).Should().BeTrue();
        current.IsFeatureEnabled(RelayFeatureFlags.Groups).Should().BeTrue();
        current.IsFeatureEnabled(RelayFeatureFlags.Blobs).Should().BeFalse();
        current.MaxMailboxes.Should().Be(500);
        current.EnvelopeTtl.Should().Be(TimeSpan.FromDays(7));
    }
}
