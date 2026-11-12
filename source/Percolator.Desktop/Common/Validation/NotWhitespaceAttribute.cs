using System;
using System.ComponentModel.DataAnnotations;

namespace Percolator.Desktop.Common.Validation;

/// <summary>Rejects null, empty, and whitespace-only strings ([VM-016] attribute validation).</summary>
public sealed class NotWhitespaceAttribute : ValidationAttribute
{
    public NotWhitespaceAttribute() : base("A non-empty value is required.")
    {
    }

    public override bool IsValid(object? value) =>
        value is string s && !string.IsNullOrWhiteSpace(s);
}
