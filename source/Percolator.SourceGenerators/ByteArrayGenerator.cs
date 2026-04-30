using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Percolator.SourceGenerators;

[Generator]
public sealed class ByteArrayGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "Percolator.SourceGenerators.ByteArrayAttribute";

    private static readonly DiagnosticDescriptor TargetMustBeRecordDiagnostic = new(
        id: "PERC001",
        title: "[ByteArray] can only be applied to records",
        messageFormat: "[ByteArray] can only be applied to record declarations",
        category: "Percolator.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor TargetMustBePartialDiagnostic = new(
        id: "PERC002",
        title: "[ByteArray] target must be partial",
        messageFormat: "The type '{0}' must be declared as a top-level, non-generic, sealed partial record",
        category: "Percolator.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor TargetMustBeSealedDiagnostic = new(
        id: "PERC003",
        title: "[ByteArray] target must be sealed",
        messageFormat: "The type '{0}' must be declared as a top-level, non-generic, sealed partial record",
        category: "Percolator.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor TargetMustBeTopLevelNonGenericDiagnostic = new(
        id: "PERC006",
        title: "[ByteArray] target must be top-level and non-generic",
        messageFormat: "The type '{0}' must be a top-level, non-generic, sealed partial record",
        category: "Percolator.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor TargetUnsupportedAccessibilityDiagnostic = new(
        id: "PERC007",
        title: "[ByteArray] target has unsupported accessibility",
        messageFormat: "The type '{0}' must be public or internal to use [ByteArray]",
        category: "Percolator.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidLengthConstraintDiagnostic = new(
        id: "PERC004",
        title: "Invalid [ByteArray] length constraints",
        messageFormat: "Invalid [ByteArray] configuration on '{0}': {1}",
        category: "Percolator.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ConflictingMemberDiagnostic = new(
        id: "PERC005",
        title: "Conflicting member with generated API",
        messageFormat: "Type '{0}' already defines member '{1}', which conflicts with [ByteArray] generated API.",
        category: "Percolator.SourceGenerators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static ctx =>
        {
            ctx.AddSource(
                "ByteArrayAttribute.g.cs",
                SourceText.From(ByteArrayAttributeSource, Encoding.UTF8));
        });

        var candidates = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: AttributeMetadataName,
                predicate: static (node, _) => node is RecordDeclarationSyntax,
                transform: static (ctx, _) => (RecordDeclarationSyntax)ctx.TargetNode)
            .Where(static r => r is not null);

        var compilationAndCandidates = context.CompilationProvider.Combine(candidates.Collect());

        context.RegisterSourceOutput(compilationAndCandidates, static (spc, pair) =>
        {
            Execute(spc, pair.Left, pair.Right);
        });
    }

    private static void Execute(SourceProductionContext context, Compilation compilation, ImmutableArray<RecordDeclarationSyntax> records)
    {
        if (records.IsDefaultOrEmpty)
            return;

        foreach (var record in records.Distinct())
        {
            var semanticModel = compilation.GetSemanticModel(record.SyntaxTree);
            if (semanticModel.GetDeclaredSymbol(record) is not INamedTypeSymbol typeSymbol)
                continue;

            if (typeSymbol.TypeKind != TypeKind.Class)
            {
                // record structs are not supported in initial version.
                context.ReportDiagnostic(Diagnostic.Create(TargetMustBeRecordDiagnostic, record.GetLocation()));
                continue;
            }

            // Initial contract: top-level + non-generic.
            if (typeSymbol.ContainingType is not null || typeSymbol.TypeParameters.Length != 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(TargetMustBeTopLevelNonGenericDiagnostic, record.GetLocation(), typeSymbol.Name));
                continue;
            }

            if (typeSymbol.DeclaredAccessibility is not Accessibility.Public and not Accessibility.Internal)
            {
                context.ReportDiagnostic(Diagnostic.Create(TargetUnsupportedAccessibilityDiagnostic, record.GetLocation(), typeSymbol.Name));
                continue;
            }

            if (typeSymbol.DeclaringSyntaxReferences.Length == 0)
                continue;

            if (!IsPartial(typeSymbol))
            {
                context.ReportDiagnostic(Diagnostic.Create(TargetMustBePartialDiagnostic, record.GetLocation(), typeSymbol.Name));
                continue;
            }

            if (!typeSymbol.IsSealed)
            {
                context.ReportDiagnostic(Diagnostic.Create(TargetMustBeSealedDiagnostic, record.GetLocation(), typeSymbol.Name));
                continue;
            }

            if (!TryGetConstraints(typeSymbol, out var constraints, out var error))
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidLengthConstraintDiagnostic, record.GetLocation(), typeSymbol.Name, error));
                continue;
            }

            if (!ValidateNoConflicts(compilation, typeSymbol, out var conflictingMember))
            {
                context.ReportDiagnostic(Diagnostic.Create(ConflictingMemberDiagnostic, record.GetLocation(), typeSymbol.Name, conflictingMember));
                continue;
            }

            var source = ByteArrayEmitter.GenerateType(typeSymbol, constraints);
            context.AddSource($"{typeSymbol.Name}.ByteArray.g.cs", SourceText.From(source, Encoding.UTF8));
        }
    }

    private static bool IsPartial(INamedTypeSymbol type)
    {
        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            if (syntaxRef.GetSyntax() is TypeDeclarationSyntax tds && tds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
                return true;
        }

        return false;
    }

    private static bool ValidateNoConflicts(Compilation compilation, INamedTypeSymbol type, out string member)
    {
        return ConflictDetector.ValidateNoConflicts(compilation, type, out member);
    }

    private readonly struct Constraints
    {
        public int? Length { get; }
        public int? MinLength { get; }
        public int? MaxLength { get; }

        public Constraints(int? length, int? minLength, int? maxLength)
        {
            Length = length;
            MinLength = minLength;
            MaxLength = maxLength;
        }
    }

    private static bool TryGetConstraints(INamedTypeSymbol type, out Constraints constraints, out string error)
    {
        return ByteArrayParser.TryGetConstraints(type, out constraints, out error);
    }

    private static string GetAccessibilityKeyword(INamedTypeSymbol type)
    {
        return type.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            _ => "internal",
        };
    }

    private static class ByteArrayParser
    {
        public static bool TryGetConstraints(INamedTypeSymbol type, out Constraints constraints, out string error)
        {
            constraints = default;
            error = string.Empty;

            var attrs = type.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == AttributeMetadataName)
                .ToArray();

            if (attrs.Length == 0)
            {
                error = "Missing [ByteArray] attribute.";
                return false;
            }

            if (attrs.Length > 1)
            {
                error = "Multiple [ByteArray] attributes are not supported.";
                return false;
            }

            var attr = attrs[0];

            int? length = null;
            int? minLength = null;
            int? maxLength = null;

            if (attr.ConstructorArguments.Length == 1)
            {
                if (attr.ConstructorArguments[0].Value is int l)
                    length = l;
            }
            else if (attr.ConstructorArguments.Length == 2)
            {
                if (attr.ConstructorArguments[0].Value is int min)
                    minLength = min;
                if (attr.ConstructorArguments[1].Value is int max)
                    maxLength = max;
            }

            foreach (var kvp in attr.NamedArguments)
            {
                var key = kvp.Key;
                var typedConstant = kvp.Value;

                if (typedConstant.Value is not int v)
                    continue;

                if (string.Equals(key, "Length", StringComparison.OrdinalIgnoreCase))
                    length = v;
                else if (string.Equals(key, "MinLength", StringComparison.OrdinalIgnoreCase))
                    minLength = v;
                else if (string.Equals(key, "MaxLength", StringComparison.OrdinalIgnoreCase))
                    maxLength = v;
            }

            if (length is null && minLength is null && maxLength is null)
            {
                error = "No constraints provided. Use ByteArray(length: N) or ByteArray(minLength: A, maxLength: B).";
                return false;
            }

            if (length is not null)
            {
                if (length <= 0)
                {
                    error = "length must be > 0.";
                    return false;
                }

                if (minLength is not null || maxLength is not null)
                {
                    error = "Do not specify length together with minLength/maxLength.";
                    return false;
                }

                constraints = new Constraints(length: length, minLength: null, maxLength: null);
                return true;
            }

            if (minLength is null || maxLength is null)
            {
                error = "Both minLength and maxLength must be provided.";
                return false;
            }

            if (minLength <= 0 || maxLength <= 0)
            {
                error = "minLength and maxLength must be > 0.";
                return false;
            }

            if (minLength > maxLength)
            {
                error = "minLength must be <= maxLength.";
                return false;
            }

            constraints = new Constraints(length: null, minLength: minLength, maxLength: maxLength);
            return true;
        }
    }

    private static class ByteArrayEmitter
    {
        public static string GenerateType(INamedTypeSymbol type, Constraints constraints)
        {
            var ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();
            var typeName = type.Name;
            var accessibility = GetAccessibilityKeyword(type);

            var expectedLengthLiteral = constraints.Length is not null
                ? constraints.Length.Value.ToString(CultureInfo.InvariantCulture)
                : "null";

            var minLengthLiteral = constraints.MinLength is not null
                ? constraints.MinLength.Value.ToString(CultureInfo.InvariantCulture)
                : "null";

            var maxLengthLiteral = constraints.MaxLength is not null
                ? constraints.MaxLength.Value.ToString(CultureInfo.InvariantCulture)
                : "null";

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("#nullable enable");

            if (ns is not null)
            {
                sb.Append("namespace ").Append(ns).AppendLine(";");
                sb.AppendLine();
            }

            sb.Append(accessibility).Append(" sealed partial record ").Append(typeName).AppendLine();
            sb.AppendLine("{");
            sb.AppendLine("    private readonly global::System.ReadOnlyMemory<byte> _value;");
            sb.AppendLine();

            sb.AppendLine($"    private const int? __ExpectedLength = {expectedLengthLiteral};");
            sb.AppendLine($"    private const int? __MinLength = {minLengthLiteral};");
            sb.AppendLine($"    private const int? __MaxLength = {maxLengthLiteral};");
            sb.AppendLine();

            sb.AppendLine("    private static bool __IsValidLength(int length)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (__ExpectedLength is not null) return length == __ExpectedLength.Value;");
            sb.AppendLine("        return length >= __MinLength!.Value && length <= __MaxLength!.Value;");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    private static void __ValidateLength(int length, string paramName)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (__IsValidLength(length)) return;");
            sb.AppendLine("        if (__ExpectedLength is not null) throw new global::System.ArgumentException($\"Expected length {__ExpectedLength.Value}\", paramName);");
            sb.AppendLine("        throw new global::System.ArgumentException($\"Expected length in range [{__MinLength!.Value}, {__MaxLength!.Value}]\", paramName);");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.Append("    private ").Append(typeName).AppendLine("(global::System.ReadOnlyMemory<byte> value)");
            sb.AppendLine("    {");
            sb.AppendLine("        _value = value;");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.Append("    public static ").Append(typeName).AppendLine(" FromBytes(byte[] bytes)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (bytes is null) throw new global::System.ArgumentNullException(nameof(bytes));");
            sb.AppendLine("        __ValidateLength(bytes.Length, nameof(bytes));");
            sb.AppendLine("        var copy = new byte[bytes.Length];");
            sb.AppendLine("        global::System.Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);");
            sb.Append("        return new ").Append(typeName).AppendLine("(copy);");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Creates an instance by taking ownership of the provided byte array without copying.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    /// <remarks>");
            sb.AppendLine("    /// This method is dangerous: the returned value may observe changes if any code mutates the array after this call.");
            sb.AppendLine("    /// Only use this when you are transferring ownership of a fresh array and no other code retains a reference.");
            sb.AppendLine("    /// </remarks>");
            sb.Append("    public static ").Append(typeName).AppendLine(" FromBytesOwned(byte[] bytes)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (bytes is null) throw new global::System.ArgumentNullException(nameof(bytes));");
            sb.AppendLine("        __ValidateLength(bytes.Length, nameof(bytes));");
            sb.Append("        return new ").Append(typeName).AppendLine("(bytes);");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.Append("    public static ").Append(typeName).AppendLine(" FromSpan(global::System.ReadOnlySpan<byte> bytes)");
            sb.AppendLine("    {");
            sb.AppendLine("        __ValidateLength(bytes.Length, nameof(bytes));");
            sb.AppendLine("        var copy = bytes.ToArray();");
            sb.Append("        return new ").Append(typeName).AppendLine("(copy);");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.Append("    public static bool TryFromBytes(byte[]? bytes, out ").Append(typeName).AppendLine("? value)");
            sb.AppendLine("    {");
            sb.AppendLine("        value = null;");
            sb.AppendLine("        if (bytes is null) return false;");
            sb.AppendLine("        if (!__IsValidLength(bytes.Length)) return false;");
            sb.AppendLine("        var copy = new byte[bytes.Length];");
            sb.AppendLine("        global::System.Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);");
            sb.Append("        value = new ").Append(typeName).AppendLine("(copy);");
            sb.AppendLine("        return true;");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.Append("    public static bool TryFromSpan(global::System.ReadOnlySpan<byte> bytes, out ").Append(typeName).AppendLine("? value)");
            sb.AppendLine("    {");
            sb.AppendLine("        value = null;");
            sb.AppendLine("        if (!__IsValidLength(bytes.Length)) return false;");
            sb.AppendLine("        var copy = bytes.ToArray();");
            sb.Append("        value = new ").Append(typeName).AppendLine("(copy);");
            sb.AppendLine("        return true;");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    public global::System.ReadOnlySpan<byte> Span => _value.Span;");
            sb.AppendLine("    public global::System.ReadOnlyMemory<byte> Memory => _value;");
            sb.AppendLine();

            sb.AppendLine("    public byte[] ToArray() => _value.ToArray();");
            sb.AppendLine();

            sb.Append("    public virtual bool Equals(").Append(typeName).AppendLine("? other)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (other is null) return false;");
            sb.AppendLine("        if (global::System.Object.ReferenceEquals(this, other)) return true;");
            sb.AppendLine("        return _value.Span.SequenceEqual(other._value.Span);");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    public override int GetHashCode()");
            sb.AppendLine("    {");
            sb.AppendLine("        unchecked");
            sb.AppendLine("        {");
            sb.AppendLine("            var hash = 17;");
            sb.AppendLine("            var span = _value.Span;");
            sb.AppendLine("            for (int i = 0; i < span.Length; i++)");
            sb.AppendLine("            {");
            sb.AppendLine("                hash = hash * 23 + span[i];");
            sb.AppendLine("            }");
            sb.AppendLine("            return hash;");
            sb.AppendLine("        }");
            sb.AppendLine("    }");

            sb.AppendLine("}");

            return sb.ToString();
        }
    }

    private static class ConflictDetector
    {
        public static bool ValidateNoConflicts(Compilation compilation, INamedTypeSymbol type, out string member)
        {
            var byteType = compilation.GetSpecialType(SpecialType.System_Byte);
            var byteArrayType = compilation.CreateArrayTypeSymbol(byteType);

            var readOnlyMemoryByte = compilation.GetTypeByMetadataName("System.ReadOnlyMemory`1")?.Construct(byteType);
            var readOnlySpanByte = compilation.GetTypeByMetadataName("System.ReadOnlySpan`1")?.Construct(byteType);

            if (readOnlyMemoryByte is null || readOnlySpanByte is null)
            {
                member = string.Empty;
                return true;
            }

            foreach (var m in type.GetMembers())
            {
                if (IsConflictingValueField(m))
                {
                    member = m.Name;
                    return false;
                }

                if (IsConflictingMemberByName(m, "Span") ||
                    IsConflictingMemberByName(m, "Memory"))
                {
                    member = m.Name;
                    return false;
                }

                if (IsConflictingToArray(m))
                {
                    member = m.Name;
                    return false;
                }

                if (IsConflictingFactory(m, "FromBytes", type, byteArrayType) ||
                    IsConflictingFactory(m, "FromBytesOwned", type, byteArrayType) ||
                    IsConflictingFactory(m, "FromSpan", type, readOnlySpanByte))
                {
                    member = m.Name;
                    return false;
                }

                if (IsConflictingTryFactory(m, "TryFromBytes", type, byteArrayType) ||
                    IsConflictingTryFactory(m, "TryFromSpan", type, readOnlySpanByte))
                {
                    member = m.Name;
                    return false;
                }
            }

            member = string.Empty;
            return true;
        }

        private static bool IsConflictingValueField(ISymbol m)
        {
            // Fields/properties cannot be overloaded; any user-defined member named _value will clash.
            return IsConflictingMemberByName(m, "_value");
        }

        private static bool IsConflictingMemberByName(ISymbol m, string name)
        {
            return string.Equals(m.Name, name, StringComparison.Ordinal);
        }

        private static bool IsConflictingToArray(ISymbol m)
        {
            return m is IMethodSymbol method &&
                   string.Equals(method.Name, "ToArray", StringComparison.Ordinal) &&
                   method.Parameters.Length == 0;
        }

        private static bool IsConflictingFactory(ISymbol m, string name, INamedTypeSymbol containingType, ITypeSymbol parameterType)
        {
            if (m is not IMethodSymbol method)
                return false;

            if (!string.Equals(method.Name, name, StringComparison.Ordinal))
                return false;

            if (!method.IsStatic)
                return false;

            if (method.Parameters.Length != 1)
                return false;

            if (!SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, parameterType))
                return false;

            return SymbolEqualityComparer.Default.Equals(method.ReturnType, containingType);
        }

        private static bool IsConflictingTryFactory(ISymbol m, string name, INamedTypeSymbol containingType, ITypeSymbol parameterType)
        {
            if (m is not IMethodSymbol method)
                return false;

            if (!string.Equals(method.Name, name, StringComparison.Ordinal))
                return false;

            if (!method.IsStatic)
                return false;

            if (method.Parameters.Length != 2)
                return false;

            if (!SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, parameterType))
                return false;

            var outParam = method.Parameters[1];
            if (outParam.RefKind != RefKind.Out)
                return false;

            if (!SymbolEqualityComparer.Default.Equals(outParam.Type, containingType))
                return false;

            return method.ReturnType.SpecialType == SpecialType.System_Boolean;
        }

    }

    private const string ByteArrayAttributeSource = @"// <auto-generated/>
#nullable enable

namespace Percolator.SourceGenerators;

[global::System.AttributeUsage(global::System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
/// <summary>
/// Marks a top-level, non-generic, sealed partial record as a byte-array-backed value object.
/// </summary>
/// <remarks>
/// Supported targets: <c>public</c>/<c>internal</c> top-level, non-generic, <c>sealed partial record</c>.
/// The generator emits a defensive-copy construction method (<c>FromBytes</c>/<c>FromSpan</c>) and an ownership-transfer method (<c>FromBytesOwned</c>).
/// </remarks>
public sealed class ByteArrayAttribute : global::System.Attribute
{
    public int? Length { get; }
    public int? MinLength { get; }
    public int? MaxLength { get; }

    public ByteArrayAttribute(int length)
    {
        Length = length;
    }

    public ByteArrayAttribute(int minLength, int maxLength)
    {
        MinLength = minLength;
        MaxLength = maxLength;
    }

    public ByteArrayAttribute()
    {
    }
}
";
}
