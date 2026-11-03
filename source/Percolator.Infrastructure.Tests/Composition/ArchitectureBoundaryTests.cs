namespace Percolator.Infrastructure.Tests.Composition;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Model;

[TestFixture]
public sealed class ArchitectureBoundaryTests
{
    [Test]
    public void CryptographicSessionAggregates_DoNotExposeRawMutableSecretProperties()
    {
        // Arrange
        var cryptographicAggregates = new[]
        {
            typeof(DirectRatchetSession),
            typeof(GroupSenderKeyRatchet),
            typeof(GroupReceiverSession)
        };

        // Act & Assert
        foreach (var aggregateType in cryptographicAggregates)
        {
            var publicProperties = aggregateType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var property in publicProperties)
            {
                typeof(ISensitiveSecret).IsAssignableFrom(property.PropertyType)
                    .Should().BeFalse(
                        $"Aggregate '{aggregateType.Name}' must encapsulate cryptographic secrets and not expose property '{property.Name}' as ISensitiveSecret.");
            }
        }
    }

    [Test]
    public void RehydrateMethods_AreRestrictedToPersistenceRepositories()
    {
        // Identify all Rehydrate methods on Domain and Application aggregates
        var assembliesToScan = new[]
        {
            typeof(Percolator.Domain.Channels.Model.DirectChannel).Assembly,
            typeof(Percolator.Application2.Delivery.OutboxJob).Assembly,
            typeof(Percolator.Apps.FileTransfer.Domain.TransferNegotiation.TransferSession).Assembly
        };

        var rehydrateMethods = new HashSet<MethodInfo>();
        foreach (var assembly in assembliesToScan)
        {
            foreach (var type in assembly.GetTypes())
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .Where(m => m.Name == "Rehydrate" || m.GetCustomAttribute<EditorBrowsableAttribute>()?.State == EditorBrowsableState.Never);

                foreach (var method in methods)
                {
                    if (method.Name == "Rehydrate")
                    {
                        rehydrateMethods.Add(method);
                    }
                }
            }
        }

        rehydrateMethods.Should().NotBeEmpty("Domain and Application aggregates must define Rehydrate methods for persistence reconstitution.");

        // Inspect production callers in Percolator.Infrastructure
        var infraAssembly = typeof(Percolator.Infrastructure.Persistence.PercolatorDbContext).Assembly;
        var violatingCallers = new List<string>();

        foreach (var type in infraAssembly.GetTypes())
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            foreach (var method in methods)
            {
                foreach (var called in GetCalledMethods(method))
                {
                    if (rehydrateMethods.Contains(called) || (called is MethodInfo mi && rehydrateMethods.Any(r => r.MethodHandle == mi.MethodHandle)))
                    {
                        string declaringNs = type.Namespace ?? string.Empty;
                        bool isAllowedRepository = declaringNs.StartsWith("Percolator.Infrastructure.Persistence.Repositories", StringComparison.Ordinal)
                            || declaringNs.StartsWith("Percolator.Infrastructure.Plugins.FileTransfer", StringComparison.Ordinal)
                            || type.Name.Contains("Repository", StringComparison.Ordinal);

                        if (!isAllowedRepository)
                        {
                            violatingCallers.Add($"{type.FullName}.{method.Name} -> {called.DeclaringType?.Name}.{called.Name}");
                        }
                    }
                }
            }
        }

        violatingCallers.Should().BeEmpty("Rehydrate methods must be invoked exclusively by persistence repositories and never by transport, gRPC, or ingress services.");
    }

    private static IEnumerable<MethodBase> GetCalledMethods(MethodBase method)
    {
        MethodBody? body;
        try
        {
            body = method.GetMethodBody();
        }
        catch
        {
            yield break;
        }

        if (body == null) yield break;

        var il = body.GetILAsByteArray();
        if (il == null) yield break;

        var module = method.Module;

        for (int i = 0; i < il.Length; i++)
        {
            byte op = il[i];
            // call is 0x28, callvirt is 0x6F
            if (op == 0x28 || op == 0x6F)
            {
                if (i + 4 < il.Length)
                {
                    int metadataToken = BitConverter.ToInt32(il, i + 1);
                    MethodBase? called = null;
                    try
                    {
                        called = module.ResolveMethod(metadataToken);
                    }
                    catch
                    {
                    }

                    if (called != null)
                    {
                        yield return called;
                    }

                    i += 4;
                }
            }
        }
    }
}
