using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Percolator.Application;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Infrastructure;
using Percolator.Infrastructure.Identity;
using static NUnit.Framework.Assert;
using System.Collections.Concurrent;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;

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

    /*[Test, CancelAfter(30000)] // 30-second timeout for the entire test
    public async Task SendChatMessage_MessageIsReceivedAndLogged()
    {
        // Create a cancellation token that will timeout after 20 seconds
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = cts.Token;

        try
        {
            // Hosts are created and started in the base SetUpAsync.
            Assert.That(SenderHost, Is.Not.Null, "SenderHost should be initialized by base setup.");
            Assert.That(ReceiverHost, Is.Not.Null, "ReceiverHost should be initialized by base setup.");

            TestContext.WriteLine("Servers started");

            // Get services from both hosts
            var senderServices = SenderHost.Services;
            var receiverServices = ReceiverHost.Services;

            // Get active identity contexts
            TestContext.WriteLine("Getting identity contexts");
            var senderIdentityContext = senderServices.GetRequiredService<ActiveIdentityContext>();
            var receiverIdentityContext = receiverServices.GetRequiredService<ActiveIdentityContext>();

            if (senderIdentityContext.Identity == null)
                TestContext.WriteLine("WARNING: Sender identity is null");
            if (receiverIdentityContext.Identity == null)
                TestContext.WriteLine("WARNING: Receiver identity is null");

            // Setup direct connection from sender to receiver
            TestContext.WriteLine("Setting up direct connection");

            // Get conversation service for creating the direct conversation
            TestContext.WriteLine("Getting conversation service");
            var conversationService = senderServices.GetRequiredService<IConversationService>();

            // Create a direct conversation, which handles peer creation, peer connection, and session establishment
            TestContext.WriteLine("Creating direct conversation");

            // Set up a DnsEndPoint for the receiver's gRPC server
            var receiverEndpoint = new System.Net.DnsEndPoint("localhost", ReceiverPort);

            // Define the message text at a higher scope so it's accessible throughout the test
            var messageText = "Hello from integration test!";

            try
            {
                // This will create the peer, peer connection, conversation, and establish the session
                var conversationId = await conversationService.CreateDirectConversationAsync(
                    receiverEndpoint, 
                    "receiver"
                );
                
                TestContext.WriteLine($"Successfully created conversation with ID {conversationId}");
                
                // DIAGNOSTIC: Verify session was established on both sides
                TestContext.WriteLine("DIAGNOSTIC: Verifying session establishment");
                
                // Check if the conversation exists in the receiver's conversation repository
                var receiverConversationRepo = receiverServices.GetRequiredService<IConversationRepository>();
                var receiverConversation = await receiverConversationRepo.GetByIdAsync(conversationId);
                TestContext.WriteLine($"Receiver has conversation: {receiverConversation != null}");
                
                // Check if the session exists in the receiver's session store
                var receiverSessionManager = receiverServices.GetRequiredService<IDirectSessionManager>();
                var senderSessionManager = senderServices.GetRequiredService<IDirectSessionManager>();
                
                // Get the peer ID for the receiver from the sender's perspective
                var senderPeerRepo = senderServices.GetRequiredService<IPeerRepository>();
                var receiverPeer = await senderPeerRepo.GetByNameAsync("receiver");
                TestContext.WriteLine($"Sender knows about receiver peer: {receiverPeer != null}");
                if (receiverPeer != null)
                {
                    TestContext.WriteLine($"Receiver peer ID: {receiverPeer.Id.Value}");
                }

                // Enhanced diagnostics for session state
                var sessionId = new Percolator.Cryptography.SessionId(conversationId.Value);
                
                // Get session stores directly for diagnostics
                var senderSessionStore = senderServices.GetRequiredService<IDoubleRatchetSessionStore>();
                var receiverSessionStore = receiverServices.GetRequiredService<IDoubleRatchetSessionStore>();
                
                // Get and compare session states
                var senderSessionState = await senderSessionStore.GetSessionStateAsync(sessionId);
                var receiverSessionState = await receiverSessionStore.GetSessionStateAsync(sessionId);
                
                TestContext.WriteLine($"Sender has session state: {senderSessionState != null}");
                TestContext.WriteLine($"Receiver has session state: {receiverSessionState != null}");
                
                // Compare key hashes to detect session asymmetry
                if (senderSessionState != null && receiverSessionState != null)
                {
                    var senderRootKey = senderSessionState.RootKey.Value != null 
                        ? Convert.ToBase64String(senderSessionState.RootKey.Value) 
                        : "null";
                    
                    var receiverRootKey = receiverSessionState.RootKey.Value != null 
                        ? Convert.ToBase64String(receiverSessionState.RootKey.Value) 
                        : "null";
                    
                    TestContext.WriteLine($"Sender root key hash: {senderRootKey}");
                    TestContext.WriteLine($"Receiver root key hash: {receiverRootKey}");
                    TestContext.WriteLine($"Root keys match: {senderRootKey == receiverRootKey}");
                    
                    // Compare chain keys
                    var senderSendingChainKey = senderSessionState.SendingChainKey?.Value != null 
                        ? Convert.ToBase64String(senderSessionState.SendingChainKey.Value) 
                        : "null";
                    
                    var receiverReceivingChainKey = receiverSessionState.ReceivingChainKey?.Value != null 
                        ? Convert.ToBase64String(receiverSessionState.ReceivingChainKey.Value) 
                        : "null";
                    
                    TestContext.WriteLine($"Sender sending chain key hash: {senderSendingChainKey}");
                    TestContext.WriteLine($"Receiver receiving chain key hash: {receiverReceivingChainKey}");
                    TestContext.WriteLine($"Chain keys match (sender's sending = receiver's receiving): {senderSendingChainKey == receiverReceivingChainKey}");
                    
                    TestContext.WriteLine($"Sender sending counter: {senderSessionState.SendingCounter}");
                    TestContext.WriteLine($"Receiver receiving counter: {receiverSessionState.ReceivingCounter}");
                }
                
                // Check peer connections
                var senderPeerConnRepo = senderServices.GetRequiredService<IPeerConnectionRepository>();
                var peerConnection = receiverPeer != null 
                    ? await senderPeerConnRepo.GetByIdAsync(new NetworkPeerId(receiverPeer.Id.Value)) 
                    : null;
                TestContext.WriteLine($"Sender has peer connection to receiver: {peerConnection != null}");
                
                // Send a message through the established connection
                TestContext.WriteLine("Sending message");
                
                // Get the message service to send the message
                TestContext.WriteLine("Getting message service");
                var senderMessageService = senderServices.GetRequiredService<IMessageService>();
                
                // Send a message to the conversation
                await senderMessageService.SendDirectMessageAsync(
                    conversationId,
                    messageText
                );
                
                TestContext.WriteLine("Message sent");
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Error in test: {ex.Message}");
                TestContext.WriteLine(ex.ToString());
                throw;
            }
            
            TestContext.WriteLine("Message sent, waiting for processing");
            
            // Wait some time for the message to be processed, but with a timeout
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(200, token);
                
                // Check logs periodically
                bool receivedMessage = LoggerProvider.ContainsLog("Received Text Message:");
                bool containsMessageText = LoggerProvider.ContainsLog(messageText);
                
                if (receivedMessage && containsMessageText)
                {
                    TestContext.WriteLine("✓ Message received log found!");
                    break;
                }
                
                TestContext.WriteLine($"Waiting for message receipt... {i+1}/50");
            }
            
            // Dump all logs for debugging
            TestContext.WriteLine("--------- ALL CAPTured LOGS ---------");
            foreach (var log in LoggerProvider.GetAllLogMessages())
            {
                TestContext.WriteLine($"LOG: {log}");
            }
            TestContext.WriteLine("-------------------------------------");
            
            // Check logs for the expected message
            bool finalReceivedMessage = LoggerProvider.ContainsLog("Received Text Message:");
            bool finalContainsMessageText = LoggerProvider.ContainsLog(messageText);
            
            TestContext.WriteLine($"Final check - Received message log found: {finalReceivedMessage}");
            TestContext.WriteLine($"Final check - Message content found in logs: {finalContainsMessageText}");
            
            Assert.That(finalReceivedMessage, "The message should have been received and logged");
            Assert.That(finalContainsMessageText, "The message content should be logged");
        }
        catch (Exception ex)
        {
            TestContext.WriteLine($"FATAL ERROR: {ex}");
            throw;
        }
    }*/
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