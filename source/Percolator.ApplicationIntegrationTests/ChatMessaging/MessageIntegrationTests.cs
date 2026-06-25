using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Percolator.Application.Identity;
using System.Collections.Concurrent;

namespace Percolator.ApplicationIntegrationTests.ChatMessaging;

[TestFixture]
[Category("ApplicationIntegrationTests")]
[NonParallelizable]
public class MessageIntegrationTests : IntegrationTestBase
{
    private List<string> _tempDirectoriesToCleanup = new();
    private int SenderPort { get; set; }
    private int ReceiverPort { get; set; }

    [SetUp]
    public override async Task SetUpAsync()
    {
        await base.SetUpAsync();
        _tempDirectoriesToCleanup.Clear();

        SenderPort = GetAvailablePort();
        ReceiverPort = GetAvailablePort();

        SenderHost = await CreateAndInitializeHostAsync(SenderPort, "Sender", "sender");
        ReceiverHost = await CreateAndInitializeHostAsync(ReceiverPort, "Receiver", "receiver");

        var senderDataDir = SenderHost.Services.GetRequiredService<IConfiguration>()["Percolator:DataDirectoryPath"];
        var receiverDataDir = ReceiverHost.Services.GetRequiredService<IConfiguration>()["Percolator:DataDirectoryPath"];

        if (!string.IsNullOrEmpty(senderDataDir)) _tempDirectoriesToCleanup.Add(senderDataDir);
        if (!string.IsNullOrEmpty(receiverDataDir)) _tempDirectoriesToCleanup.Add(receiverDataDir);
        
        await SenderHost.StartAsync();
        await ReceiverHost.StartAsync();
    }

    [TearDown]
    public override async Task TearDownAsync()
    {
        // First, stop the hosts to release ports.
        await base.TearDownAsync();

        // Then, clean up the directories.
        foreach (var dir in _tempDirectoriesToCleanup)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Warning: Could not clean up temp directory {dir}. Reason: {ex.Message}");
            }
        }
    }

    [Test, CancelAfter(30000)] // 30-second timeout for the entire test
    public void Test2_IdentityCreation_ShouldComplete()
    {
        // Test just the identity creation part
        TestContext.WriteLine("Starting Test2_IdentityCreation_ShouldComplete");

        try
        {
            // Hosts are created and started in the base SetUpAsync.
            Assert.That(SenderHost, Is.Not.Null, "SenderHost should be initialized by base setup.");
            Assert.That(ReceiverHost, Is.Not.Null, "ReceiverHost should be initialized by base setup.");
            
            TestContext.WriteLine("Hosts started");

            // Get services from both hosts
            var senderServices = SenderHost.Services;
            var receiverServices = ReceiverHost.Services;

            // Get active identity contexts
            TestContext.WriteLine("Getting identity contexts");
            var senderIdentityContext = senderServices.GetRequiredService<ActiveIdentityContext>();
            var receiverIdentityContext = receiverServices.GetRequiredService<ActiveIdentityContext>();

            TestContext.WriteLine($"Sender identity: {senderIdentityContext.Identity?.Id}");
            TestContext.WriteLine($"Receiver identity: {receiverIdentityContext.Identity?.Id}");

            Assert.That(senderIdentityContext.Identity, Is.Not.Null, "Sender identity should be loaded");
            Assert.That(receiverIdentityContext.Identity, Is.Not.Null, "Receiver identity should be loaded");

            TestContext.WriteLine("Identity creation test passed!");
        }
        catch (Exception ex)
        {
            TestContext.WriteLine($"FATAL ERROR: {ex}");
            throw;
        }
    }

   
}

public class TestLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, TestLogger> _loggers = new();
    private readonly ConcurrentQueue<string> _logMessages = new();

    public ILogger CreateLogger(string categoryName)
    {
        return _loggers.GetOrAdd(categoryName, name => new TestLogger(name, _logMessages));
    }

    public bool ContainsLog(string message)
    {
        return _logMessages.Any(log => log.Contains(message));
    }

    public IEnumerable<string> GetAllLogMessages()
    {
        return _logMessages.ToList();
    }

    public void Dispose()
    {
        _loggers.Clear();
        _logMessages.Clear();
    }
}

public class TestLogger : ILogger
{
    private readonly string _name;
    private readonly ConcurrentQueue<string> _logMessages;

    public TestLogger(string name, ConcurrentQueue<string> logMessages)
    {
        _name = name;
        _logMessages = logMessages;
    }

    public IDisposable BeginScope<TState>(TState state)
    {
        return null!;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        _logMessages.Enqueue(message);
        TestContext.WriteLine($"[{logLevel}] {_name}: {message}");
        if (exception != null)
        {
            TestContext.WriteLine(exception.ToString());
        }
    }
}
