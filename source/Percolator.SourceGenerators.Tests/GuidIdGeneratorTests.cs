using System.Collections.Immutable;
using System.Reflection;
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
        var (sources, diagnostics, _) = RunGenerator(@"
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
        generated.Should().Contain("public static AccountId FromBytes(global::System.ReadOnlySpan<byte> bytes) => new(new global::System.Guid(bytes, bigEndian: true));");
        generated.Should().Contain("public bool TryWriteBytes(global::System.Span<byte> destination) => Value.TryWriteBytes(destination, bigEndian: true, out _);");
        generated.Should().Contain("public byte[] ToByteArray() => Value.ToByteArray(bigEndian: true);");
        generated.Should().Contain("public global::System.Guid Value { get; }");
        generated.Should().NotContain("operator");
    }

    [Test]
    public void Generates_SequentialTimeBased_WhenSpecified()
    {
        var (sources, diagnostics, _) = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.GuidId(Percolator.SourceGenerators.GuidIdKind.SequentialTimeBased)]
public readonly partial record struct MessageId;
");

        diagnostics.Should().BeEmpty();
        var generated = sources.Single(s => s.Contains("MessageId", StringComparison.Ordinal));
        generated.Should().Contain("public static MessageId New() => new(global::System.Guid.CreateVersion7());");
        generated.Should().Contain("public static MessageId New(global::System.DateTimeOffset timestamp) => new(global::System.Guid.CreateVersion7(timestamp));");
        generated.Should().Contain("public static MessageId FromBytes(global::System.ReadOnlySpan<byte> bytes) => new(new global::System.Guid(bytes, bigEndian: true));");
        generated.Should().Contain("public bool TryWriteBytes(global::System.Span<byte> destination) => Value.TryWriteBytes(destination, bigEndian: true, out _);");
        generated.Should().Contain("public byte[] ToByteArray() => Value.ToByteArray(bigEndian: true);");
        generated.Should().NotContain("operator");
    }

    [Test]
    public void Works_WhenTypeDeclaresPrimaryConstructor()
    {
        var (sources, diagnostics, _) = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.GuidId]
public readonly partial record struct TokenId(System.Guid Value);
");

        diagnostics.Should().BeEmpty();
        var generated = sources.Single(s => s.Contains("TokenId", StringComparison.Ordinal));
        // Should not duplicate Value property or primary constructor
        generated.Should().NotContain("public global::System.Guid Value { get; }");
        generated.Should().Contain("public static TokenId New() => new(global::System.Guid.NewGuid());");
        generated.Should().NotContain("operator");
    }

    [Test]
    public void EmitsDiagnostic_WhenAppliedToClass()
    {
        var (sources, diagnostics, _) = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.GuidId]
public sealed partial class InvalidClass;
");

        diagnostics.Should().Contain(d => d.Id == "PERC101");
    }

    [Test]
    public void RoundTrip_And_HexParity_ExecutionTest()
    {
        var (_, diagnostics, outputCompilation) = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.GuidId]
public readonly partial record struct CustomerId;

public static class CustomerIdTestHelper
{
    public static CustomerId FromBytes(byte[] bytes) => CustomerId.FromBytes(bytes);
    public static bool TryWriteBytes(CustomerId id, byte[] destination) => id.TryWriteBytes(destination);
}
");

        diagnostics.Should().BeEmpty();

        using var ms = new MemoryStream();
        var emitResult = outputCompilation.Emit(ms);
        emitResult.Success.Should().BeTrue();

        ms.Seek(0, SeekOrigin.Begin);
        var asm = Assembly.Load(ms.ToArray());
        var customerIdType = asm.GetType("Demo.CustomerId")!;
        var helperType = asm.GetType("Demo.CustomerIdTestHelper")!;

        var originalGuid = Guid.Parse("018f3a5e-4c7b-7a81-9b34-8c9d0e1f2a3b");
        var fromGuidMethod = customerIdType.GetMethod("FromGuid", BindingFlags.Public | BindingFlags.Static)!;
        var instance = fromGuidMethod.Invoke(null, new object[] { originalGuid })!;

        // 1. ToByteArray
        var toByteArrayMethod = customerIdType.GetMethod("ToByteArray", BindingFlags.Public | BindingFlags.Instance)!;
        var bytes = (byte[])toByteArrayMethod.Invoke(instance, null)!;
        bytes.Should().HaveCount(16);
        bytes.Should().Equal(originalGuid.ToByteArray(bigEndian: true));

        // 2. Hex Parity: Big-Endian byte sequence must match hex string without hyphens
        var hex = Convert.ToHexString(bytes);
        hex.Should().Be("018F3A5E4C7B7A819B348C9D0E1F2A3B");

        // 3. Round-Trip: FromBytes(bytes) == instance via helper
        var helperFromBytesMethod = helperType.GetMethod("FromBytes", BindingFlags.Public | BindingFlags.Static)!;
        var rehydrated = helperFromBytesMethod.Invoke(null, new object[] { bytes })!;
        rehydrated.Should().Be(instance);

        // 4. TryWriteBytes via helper
        var destination = new byte[16];
        var helperTryWriteMethod = helperType.GetMethod("TryWriteBytes", BindingFlags.Public | BindingFlags.Static)!;
        var writtenSuccess = (bool)helperTryWriteMethod.Invoke(null, new object[] { instance, destination })!;
        writtenSuccess.Should().BeTrue();
        destination.Should().Equal(bytes);
    }

    [Test]
    public void SequentialTimeBased_Monotonicity_ExecutionTest()
    {
        var (_, diagnostics, outputCompilation) = RunGenerator(@"
namespace Demo;

[Percolator.SourceGenerators.GuidId(Percolator.SourceGenerators.GuidIdKind.SequentialTimeBased)]
public readonly partial record struct EventId;
");

        diagnostics.Should().BeEmpty();

        using var ms = new MemoryStream();
        var emitResult = outputCompilation.Emit(ms);
        emitResult.Success.Should().BeTrue();

        ms.Seek(0, SeekOrigin.Begin);
        var asm = Assembly.Load(ms.ToArray());
        var eventIdType = asm.GetType("Demo.EventId")!;

        var newWithTimeMethod = eventIdType.GetMethod("New", BindingFlags.Public | BindingFlags.Static, new[] { typeof(DateTimeOffset) })!;
        var toByteArrayMethod = eventIdType.GetMethod("ToByteArray", BindingFlags.Public | BindingFlags.Instance)!;

        var t1 = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddMinutes(5);

        var id1 = newWithTimeMethod.Invoke(null, new object[] { t1 })!;
        var id2 = newWithTimeMethod.Invoke(null, new object[] { t2 })!;

        var bytes1 = (byte[])toByteArrayMethod.Invoke(id1, null)!;
        var bytes2 = (byte[])toByteArrayMethod.Invoke(id2, null)!;

        // Big-endian ensures that earlier timestamps sort lexicographically BEFORE later timestamps in SQLite memcmp
        bytes1.AsSpan().SequenceCompareTo(bytes2.AsSpan()).Should().BeNegative();
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

        var generator = new GuidIdGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { generator.AsSourceGenerator() }, parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

        var runResult = driver.GetRunResult();

        var sources = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(gs => gs.SourceText.ToString())
            .ToList();

        return (sources, runResult.Diagnostics.AddRange(diagnostics), outputCompilation);
    }
}
