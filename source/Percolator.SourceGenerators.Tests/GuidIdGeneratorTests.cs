using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Percolator.SourceGenerators.Tests;

[TestFixture]
public sealed class GuidIdGeneratorTests
{
    [Test]
    public void Generates_CryptographicRandom_ByDefault()
    {
        var (sources, diagnostics) = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.GuidId]
public readonly partial record struct AccountId;
");

        diagnostics.Should().BeEmpty();
        var generated = sources.Single(s => s.Contains("AccountId", StringComparison.Ordinal));
        generated.Should().Contain("public static AccountId New() => new(global::System.Guid.NewGuid());");
        generated.Should().NotContain("public static AccountId Empty");
        generated.Should().Contain("public bool IsValid => Value != global::System.Guid.Empty;");
        generated.Should().Contain("public void EnsureValid(string? paramName = null)");
        generated.Should().Contain("public static AccountId FromGuid(global::System.Guid value) => new(value);");
        generated.Should().Contain("public static AccountId FromBytes(global::System.ReadOnlySpan<byte> bytes) => new(new global::System.Guid(bytes));");
        generated.Should().Contain("public bool TryWriteBytes(global::System.Span<byte> destination) => Value.TryWriteBytes(destination);");
        generated.Should().Contain("public global::System.Guid Value { get; }");
    }

    [Test]
    public void Generates_SequentialTimeBased_WhenSpecified()
    {
        var (sources, diagnostics) = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.GuidId(Percolator.SourceGenerators.GuidIdKind.SequentialTimeBased)]
public readonly partial record struct MessageId;
");

        diagnostics.Should().BeEmpty();
        var generated = sources.Single(s => s.Contains("MessageId", StringComparison.Ordinal));
        generated.Should().Contain("public static MessageId New() => new(global::System.Guid.CreateVersion7());");
        generated.Should().Contain("public static MessageId New(global::System.DateTimeOffset timestamp) => new(global::System.Guid.CreateVersion7(timestamp));");
    }

    [Test]
    public void Works_WhenTypeDeclaresPrimaryConstructor()
    {
        var (sources, diagnostics) = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.GuidId]
public readonly partial record struct TokenId(System.Guid Value);
");

        diagnostics.Should().BeEmpty();
        var generated = sources.Single(s => s.Contains("TokenId", StringComparison.Ordinal));
        // Should not duplicate Value property or primary constructor
        generated.Should().NotContain("public global::System.Guid Value { get; }");
        generated.Should().Contain("public static TokenId New() => new(global::System.Guid.NewGuid());");
    }

    [Test]
    public void EmitsDiagnostic_WhenAppliedToClass()
    {
        var (sources, diagnostics) = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.GuidId]
public sealed partial class InvalidClass;
");

        diagnostics.Should().Contain(d => d.Id == "PERC101");
    }

    private static (List<string> GeneratedSources, ImmutableArray<Diagnostic> Diagnostics) RunGenerator(string userSource)
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
        };

        var compilation = CSharpCompilation.Create(
            assemblyName: "Demo",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var generator = new GuidIdGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { generator.AsSourceGenerator() }, parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var runResult = driver.GetRunResult();

        var sources = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(gs => gs.SourceText.ToString())
            .ToList();

        return (sources, runResult.Diagnostics.AddRange(diagnostics));
    }
}
