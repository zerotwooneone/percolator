using FluentAssertions;
using NUnit.Framework;
using Percolator.Apps.Chat.Services;

namespace Percolator.Apps.Chat.Tests.Models;

[TestFixture]
public class SteppedPaddingTests
{
    [TestCase(0, 256)]
    [TestCase(1, 256)]
    [TestCase(256, 256)]
    [TestCase(257, 512)]
    [TestCase(512, 512)]
    [TestCase(513, 1024)]
    [TestCase(1024, 1024)]
    [TestCase(1025, 2048)]
    [TestCase(2048, 2048)]
    [TestCase(2049, 4096)]
    [TestCase(4096, 4096)]
    [TestCase(4097, 8192)]
    [TestCase(8192, 8192)]
    [TestCase(8193, 12288)]
    public void CalculatePaddedLength_ReturnsExpectedBucket(int unpaddedLength, int expectedPaddedLength)
    {
        // Act
        var result = SteppedPadding.CalculatePaddedLength(unpaddedLength);

        // Assert
        result.Should().Be(expectedPaddedLength);
    }
}
