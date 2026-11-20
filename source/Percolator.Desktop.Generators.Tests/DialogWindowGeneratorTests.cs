using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NUnit.Framework;

namespace Percolator.Desktop.Generators.Tests;

[TestFixture]
public sealed class DialogWindowGeneratorTests
{
    [Test]
    public void Emits_DialogForAttribute_post_initialization()
    {
        var result = RunGenerator(@"
namespace Demo;

public class Empty;
");

        result.GeneratedSources.Should().Contain(s => s.Contains("class DialogForAttribute", StringComparison.Ordinal));
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Test]
    public void Generates_registry_for_annotated_dialog_windows()
    {
        var result = RunGenerator(@"
using Percolator.Desktop.Common.Windowing;

namespace Demo.ViewModels
{
    public class MyTestDialogViewModel {}
}

namespace Demo.Views
{
    [DialogFor(typeof(Demo.ViewModels.MyTestDialogViewModel))]
    public class MyTestDialogWindow {}
}
");

        var registrySource = result.GeneratedSources.SingleOrDefault(s => s.Contains("class GeneratedDialogRegistry", StringComparison.Ordinal));
        registrySource.Should().NotBeNull();
        registrySource.Should().Contain("global::Demo.ViewModels.MyTestDialogViewModel => new global::Demo.Views.MyTestDialogWindow()");
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Test]
    public void Generates_registry_ordered_deterministically_for_multiple_dialogs()
    {
        var result = RunGenerator(@"
using Percolator.Desktop.Common.Windowing;

namespace Demo.ViewModels
{
    public class BViewModel {}
    public class AViewModel {}
}

namespace Demo.Views
{
    [DialogFor(typeof(Demo.ViewModels.BViewModel))]
    public class BWindow {}

    [DialogFor(typeof(Demo.ViewModels.AViewModel))]
    public class AWindow {}
}
");

        var registrySource = result.GeneratedSources.SingleOrDefault(s => s.Contains("class GeneratedDialogRegistry", StringComparison.Ordinal));
        registrySource.Should().NotBeNull();

        var indexOfA = registrySource!.IndexOf("global::Demo.ViewModels.AViewModel", StringComparison.Ordinal);
        var indexOfB = registrySource.IndexOf("global::Demo.ViewModels.BViewModel", StringComparison.Ordinal);

        indexOfA.Should().BeLessThan(indexOfB, "registry entries must be sorted alphabetically by ViewModel name");
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    private static GeneratorRunResult RunGenerator(string userSource)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var syntaxTree = CSharpSyntaxTree.ParseText(SourceText.From(userSource, System.Text.Encoding.UTF8), parseOptions);

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            assemblyName: "Demo",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var generator = new DialogWindowGenerator();
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

    private sealed record GeneratorRunResult(
        ImmutableArray<Diagnostic> Diagnostics,
        IReadOnlyList<SyntaxTree> GeneratedTrees,
        IReadOnlyList<string> GeneratedSources);
}
