namespace Percolator.Crypto.Tests;

public class MKSkippedKeyTests
{
    [Test]
    public void EqualityWorks()
    {
        var one = new DoubleRatchetModel.MKSkippedKey
        {
            MessagePublicKey =
            [
                1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
                29, 30, 31, 32
            ],
            MessageNumber = 100
        };
        var two = new DoubleRatchetModel.MKSkippedKey
        {
            MessagePublicKey =
            [
                1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28,
                29, 30, 31, 32
            ],
            MessageNumber = 100
        };
        Assert.That(one,Is.EqualTo(two));
        Assert.That(one.GetHashCode(),Is.EqualTo(two.GetHashCode()));
    }
}