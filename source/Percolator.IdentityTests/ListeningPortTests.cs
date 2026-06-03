using Percolator.Identity.Model;

namespace Percolator.IdentityTests;

[TestFixture]
public sealed class ListeningPortTests
{
    [Test]
    public void Constructor_GivenValueBelow1024_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListeningPort(1023));
    }

    [Test]
    public void Constructor_GivenValueAbove65535_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ListeningPort(65536));
    }

    [Test]
    public void Constructor_GivenValidValue_SetsValue()
    {
        var port = new ListeningPort(5000);
        Assert.That(port.Value, Is.EqualTo(5000));
    }

    [Test]
    public void Constructor_GivenMinimumValidValue_SetsValue()
    {
        var port = new ListeningPort(ListeningPort.MinPort);
        Assert.That(port.Value, Is.EqualTo(ListeningPort.MinPort));
    }

    [Test]
    public void Constructor_GivenMaximumValidValue_SetsValue()
    {
        var port = new ListeningPort(ListeningPort.MaxPort);
        Assert.That(port.Value, Is.EqualTo(ListeningPort.MaxPort));
    }

    [Test]
    public void Value_ReturnsIntValue()
    {
        ListeningPort port = new ListeningPort(5000);
        Assert.That(port.Value, Is.EqualTo(5000));
    }

    [Test]
    public void ToString_ReturnsPortNumberAsString()
    {
        ListeningPort port = new ListeningPort(5000);
        Assert.That(port.ToString(), Is.EqualTo("5000"));
    }
}
