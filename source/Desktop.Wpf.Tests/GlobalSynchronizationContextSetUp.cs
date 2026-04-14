using System.Threading;
using NUnit.Framework;

namespace Desktop.Wpf.Tests;

[SetUpFixture]
public sealed class GlobalSynchronizationContextSetUp
{
    private SynchronizationContext? _original;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _original = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new TestSynchronizationContext());
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        SynchronizationContext.SetSynchronizationContext(_original);
    }
}
