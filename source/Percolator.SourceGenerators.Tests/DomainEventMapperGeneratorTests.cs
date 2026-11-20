using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;

namespace Percolator.SourceGenerators.Tests;

[TestFixture]
public sealed class DomainEventMapperGeneratorTests
{
    [Test]
    public void Generator_EmitsFromDomainFactory_WithMatchingProperties()
    {
        var (sources, diagnostics, _) = RunGenerator(@"
namespace Percolator.Domain.Common
{
    public interface IDomainEvent { global::System.Guid EventId { get; } global::System.DateTimeOffset OccurredOnUtc { get; } }
}

namespace Demo
{
    public sealed record ChannelRenamedDomainEvent(
        global::System.Guid EventId,
        global::System.DateTimeOffset OccurredOnUtc,
        string OldName,
        string NewName) : Percolator.Domain.Common.IDomainEvent;

    [Percolator.SourceGenerators.MapFromDomainEvent(typeof(ChannelRenamedDomainEvent))]
    public sealed partial record ChannelRenamedApplicationEvent;
}
");

        diagnostics.Should().BeEmpty();
        var generated = sources.Single(s => s.Contains("partial record ChannelRenamedApplicationEvent", StringComparison.Ordinal));
        generated.Should().Contain("public string OldName { get; init; }");
        generated.Should().Contain("public string NewName { get; init; }");
        generated.Should().Contain("public static ChannelRenamedApplicationEvent FromDomain(global::Demo.ChannelRenamedDomainEvent domainEvent)");
        generated.Should().Contain("OldName = domainEvent.OldName,");
        generated.Should().Contain("NewName = domainEvent.NewName,");
        generated.Should().Contain("CustomMap(domainEvent, ref result);");
        generated.Should().Contain("static partial void CustomMap(global::Demo.ChannelRenamedDomainEvent source, ref ChannelRenamedApplicationEvent target);");
    }

    [Test]
    public void Generator_UnboxesValueObjects_ExtractingPrimitiveValue()
    {
        var (sources, diagnostics, _) = RunGenerator(@"
namespace Percolator.Domain.Common
{
    public interface IDomainEvent { global::System.Guid EventId { get; } global::System.DateTimeOffset OccurredOnUtc { get; } }
}

namespace Demo
{
    public readonly record struct ChannelId(global::System.Guid Value);
    public readonly record struct DeviceId(uint Value);

    public sealed record MemberJoinedDomainEvent(
        global::System.Guid EventId,
        global::System.DateTimeOffset OccurredOnUtc,
        ChannelId ChannelId,
        DeviceId? DeviceId) : Percolator.Domain.Common.IDomainEvent;

    [Percolator.SourceGenerators.MapFromDomainEvent(typeof(MemberJoinedDomainEvent))]
    public sealed partial record MemberJoinedApplicationEvent;
}
");

        diagnostics.Should().BeEmpty();
        var generated = sources.Single(s => s.Contains("partial record MemberJoinedApplicationEvent", StringComparison.Ordinal));
        generated.Should().Contain("public global::System.Guid ChannelId { get; init; }");
        generated.Should().Contain("public uint? DeviceId { get; init; }");
        generated.Should().Contain("ChannelId = domainEvent.ChannelId.Value,");
        generated.Should().Contain("DeviceId = domainEvent.DeviceId?.Value,");
    }

    [Test]
    public void Generator_WhenTargetHasCustomMapPartialMethod_EmitsPartialHookInvocation()
    {
        var (sources, diagnostics, _) = RunGenerator(@"
namespace Percolator.Domain.Common
{
    public interface IDomainEvent { global::System.Guid EventId { get; } global::System.DateTimeOffset OccurredOnUtc { get; } }
}

namespace Demo
{
    public sealed record UserBannedDomainEvent(
        global::System.Guid EventId,
        global::System.DateTimeOffset OccurredOnUtc,
        string Reason) : Percolator.Domain.Common.IDomainEvent;

    [Percolator.SourceGenerators.MapFromDomainEvent(typeof(UserBannedDomainEvent))]
    public sealed partial record UserBannedApplicationEvent
    {
        public string FormattedReason { get; init; } = string.Empty;

        static partial void CustomMap(UserBannedDomainEvent source, ref UserBannedApplicationEvent target)
        {
            target = target with { FormattedReason = ""[BANNED] "" + source.Reason };
        }
    }
}
");

        diagnostics.Should().BeEmpty();
        var generated = sources.Single(s => s.Contains("partial record UserBannedApplicationEvent", StringComparison.Ordinal));
        generated.Should().Contain("CustomMap(domainEvent, ref result);");
        generated.Should().Contain("static partial void CustomMap(global::Demo.UserBannedDomainEvent source, ref UserBannedApplicationEvent target);");
    }

    [Test]
    public void Generator_EmitsToApplicationEventPatternMatch_MappingAllDecoratedEvents()
    {
        var (sources, diagnostics, _) = RunGenerator(@"
namespace Percolator.Domain.Common
{
    public interface IDomainEvent { global::System.Guid EventId { get; } global::System.DateTimeOffset OccurredOnUtc { get; } }
}

namespace Percolator.Application2.Ports
{
    public interface IApplicationEvent { global::System.Guid EventId { get; } global::System.DateTimeOffset OccurredOnUtc { get; } }
}

namespace Demo
{
    public sealed record EventA(global::System.Guid EventId, global::System.DateTimeOffset OccurredOnUtc) : Percolator.Domain.Common.IDomainEvent;
    public sealed record EventB(global::System.Guid EventId, global::System.DateTimeOffset OccurredOnUtc) : Percolator.Domain.Common.IDomainEvent;

    [Percolator.SourceGenerators.MapFromDomainEvent(typeof(EventA))]
    public sealed partial record AppEventA : Percolator.Application2.Ports.IApplicationEvent;

    [Percolator.SourceGenerators.MapFromDomainEvent(typeof(EventB))]
    public sealed partial record AppEventB : Percolator.Application2.Ports.IApplicationEvent;
}
");

        diagnostics.Should().BeEmpty();
        var extensions = sources.Single(s => s.Contains("class DomainEventMapperExtensions", StringComparison.Ordinal));
        extensions.Should().Contain("public static global::Percolator.Application2.Ports.IApplicationEvent? ToApplicationEvent(this global::Percolator.Domain.Common.IDomainEvent domainEvent)");
        extensions.Should().Contain("global::Demo.EventA e => global::Demo.AppEventA.FromDomain(e),");
        extensions.Should().Contain("global::Demo.EventB e => global::Demo.AppEventB.FromDomain(e),");
    }

    [Test]
    public void Generator_WhenDomainPropertiesAreUnmatched_OmitsSilentlyWithoutDiagnosticErrors()
    {
        var (sources, diagnostics, _) = RunGenerator(@"
namespace Percolator.Domain.Common
{
    public interface IDomainEvent { global::System.Guid EventId { get; } global::System.DateTimeOffset OccurredOnUtc { get; } }
}

namespace Demo
{
    public sealed record ComplexDomainEvent(
        global::System.Guid EventId,
        global::System.DateTimeOffset OccurredOnUtc,
        string SecretData,
        int Counter) : Percolator.Domain.Common.IDomainEvent;

    [Percolator.SourceGenerators.MapFromDomainEvent(typeof(ComplexDomainEvent))]
    public sealed partial record SimpleApplicationEvent
    {
        // Explicitly only declares Counter - SecretData is omitted
        public int Counter { get; init; }
    }
}
");

        diagnostics.Should().BeEmpty();
        var generated = sources.Single(s => s.Contains("partial record SimpleApplicationEvent", StringComparison.Ordinal));
        generated.Should().Contain("Counter = domainEvent.Counter,");
    }

    [Test]
    public void Generator_WhenTargetIsClassNotRecord_ReportsPERC204Diagnostic()
    {
        var (sources, diagnostics, _) = RunGenerator(@"
namespace Percolator.Domain.Common
{
    public interface IDomainEvent { global::System.Guid EventId { get; } global::System.DateTimeOffset OccurredOnUtc { get; } }
}

namespace Demo
{
    public sealed record SomeDomainEvent(
        global::System.Guid EventId,
        global::System.DateTimeOffset OccurredOnUtc) : Percolator.Domain.Common.IDomainEvent;

    [Percolator.SourceGenerators.MapFromDomainEvent(typeof(SomeDomainEvent))]
    public sealed partial class ClassApplicationEvent;
}
");

        diagnostics.Should().ContainSingle(d => d.Id == "PERC204");
        diagnostics[0].GetMessage().Should().Contain("must be declared as a record");
    }

    private static (List<string> GeneratedSources, ImmutableArray<Diagnostic> Diagnostics, Compilation OutputCompilation) RunGenerator(string userSource)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(userSource, System.Text.Encoding.UTF8), parseOptions);

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Guid).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(DateTimeOffset).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Runtime.GCSettings).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location),
        };

        var compilation = CSharpCompilation.Create(
            assemblyName: "Demo",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var generator = new DomainEventMapperGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { generator.AsSourceGenerator() }, parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var runResult = driver.GetRunResult();

        var sources = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(gs => gs.SourceText.ToString())
            .ToList();

        return (sources, runResult.Diagnostics, outputCompilation);
    }
}
