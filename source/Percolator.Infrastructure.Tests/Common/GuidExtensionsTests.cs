namespace Percolator.Infrastructure.Tests.Common;

using Percolator.Infrastructure.Common;

[TestFixture]
public class GuidExtensionsTests
{
    [Test]
    public void ToBigEndianByteArray_Formats_ValidRfc4122ByteOrder()
    {
        // Arrange: RFC 4122 test vector
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        byte[] expectedBytes =
        [
            0x00, 0x11, 0x22, 0x33,
            0x44, 0x55,
            0x66, 0x77,
            0x88, 0x99,
            0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff
        ];

        // Act
        byte[] actualBytes = guid.ToBigEndianByteArray();

        // Assert
        actualBytes.Should().Equal(expectedBytes);
    }
}
