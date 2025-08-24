using NUnit.Framework;

namespace Percolator.ApplicationIntegrationTests.ChatMessaging
{
    [TestFixture]
    public class MinimalTest
    {
        [Test, CancelAfter(5000)] // 5-second timeout
        public void SimpleTest_ShouldComplete()
        {
            // Extremely simple test that should complete immediately
            TestContext.WriteLine("Starting minimal test");
            TestContext.WriteLine("Minimal test completed!");
            Assert.Pass("Minimal test passed");
        }

        [Test, CancelAfter(5000)] // 5-second timeout
        public async Task SimpleAsyncTest_ShouldComplete()
        {
            // Extremely simple async test
            TestContext.WriteLine("Starting minimal async test");
            await Task.Delay(100); // Small delay to ensure async behavior
            TestContext.WriteLine("Minimal async test completed!");
            Assert.Pass("Minimal async test passed");
        }
    }
}
