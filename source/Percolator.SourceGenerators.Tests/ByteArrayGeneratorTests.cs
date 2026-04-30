using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Percolator.SourceGenerators;

namespace Percolator.SourceGenerators.Tests;

[TestFixture]
public sealed class ByteArrayGeneratorTests
{
    [Test]
    public void Generates_without_global_using()
    {
        var result = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.ByteArray(length: 3)]
public sealed partial record Foo;
");

        result.GeneratedTrees.Should().NotBeEmpty();
        result.GeneratedSources.Should().NotContain(s => s.Contains("global using Percolator.SourceGenerators;", StringComparison.Ordinal));
    }

    [Test]
    public void Emits_correct_accessibility_for_internal_type()
    {
        var result = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.ByteArray(length: 3)]
internal sealed partial record Foo;
");

        var generated = result.GeneratedSources.Single(s => s.Contains("sealed partial record Foo", StringComparison.Ordinal));
        generated.Should().Contain("internal sealed partial record Foo");
    }

    [Test]
    public void Conflict_detection_is_signature_based_allows_overload()
    {
        var result = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.ByteArray(length: 3)]
public sealed partial record Foo
{
    public static Foo FromBytes(byte[] bytes, int extra) => throw new System.NotImplementedException();
}
");

        result.Diagnostics.Should().NotContain(d => d.Id == "PERC005");
    }

    [Test]
    public void Conflict_detection_blocks_exact_signature_conflict()
    {
        var result = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.ByteArray(length: 3)]
public sealed partial record Foo
{
    public static Foo FromBytes(byte[] bytes) => throw new System.NotImplementedException();
}
");

        result.Diagnostics.Should().Contain(d => d.Id == "PERC005");
    }

    [Test]
    public void Conflict_detection_blocks_property_name_conflict_even_with_different_type()
    {
        var result = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.ByteArray(length: 3)]
public sealed partial record Foo
{
    public int Span { get; }
}
");

        result.Diagnostics.Should().Contain(d => d.Id == "PERC005");
    }

    private static GeneratorRunResult RunGenerator(string userSource)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(userSource, System.Text.Encoding.UTF8), parseOptions);

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ReadOnlyMemory<byte>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Runtime.GCSettings).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            assemblyName: "Demo",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var generator = new ByteArrayGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { generator.AsSourceGenerator() }, parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var runResult = driver.GetRunResult();

        var generatedSources = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(gs => gs.SourceText.ToString())
            .ToList();

        var generatedTrees = outputCompilation.SyntaxTrees.Except(new[] { syntaxTree }).ToList();

        var allDiagnostics = runResult.Diagnostics.AddRange(diagnostics);
        return new GeneratorRunResult(allDiagnostics, generatedTrees, generatedSources);
    }

    private sealed record GeneratorRunResult(ImmutableArray<Diagnostic> Diagnostics, IReadOnlyList<SyntaxTree> GeneratedTrees, IReadOnlyList<string> GeneratedSources);
}
