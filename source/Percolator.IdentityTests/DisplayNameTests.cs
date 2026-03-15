using Percolator.Identity.Model;

namespace Percolator.IdentityTests;

[TestFixture]
public class DisplayNameTests
{
    [Test]
    public void Valid_names_are_accepted()
    {
        var dn = new DisplayName("Alice 123");
        Assert.That(dn.Value, Is.EqualTo("Alice 123"));
    }

    [Test]
    public void Empty_or_whitespace_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new DisplayName(""));
        Assert.Throws<ArgumentException>(() => new DisplayName("   "));
    }

    [Test]
    public void Excessively_long_name_is_rejected()
    {
        var longName = new string('a', 256);
        Assert.Throws<ArgumentException>(() => new DisplayName(longName));
    }
}
