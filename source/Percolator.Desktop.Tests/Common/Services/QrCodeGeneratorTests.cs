// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/ViewModel.spec.md before modifying this file.

using System;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Desktop.Common.Services;

namespace Percolator.Desktop.Tests.Common.Services;

[TestFixture]
public sealed class QrCodeGeneratorTests
{
    private QrCodeGenerator _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _sut = new QrCodeGenerator();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void GeneratePngBytes_WithNullOrWhiteSpaceContent_ReturnsEmptyByteArray(string? content)
    {
        var result = _sut.GeneratePngBytes(content!);

        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Test]
    public void GeneratePngBytes_WithValidContent_ReturnsValidPngBytes()
    {
        var content = "percolator://invite?test=1";

        var result = _sut.GeneratePngBytes(content);

        result.Should().NotBeNull();
        result.Should().NotBeEmpty();
        // PNG magic header: 0x89, 0x50 ('P'), 0x4E ('N'), 0x47 ('G')
        result.Length.Should().BeGreaterThan(8);
        result[0].Should().Be(0x89);
        result[1].Should().Be(0x50);
        result[2].Should().Be(0x4E);
        result[3].Should().Be(0x47);
    }
}
