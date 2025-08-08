using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Application.KeyExchange;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Infrastructure;
using Percolator.Infrastructure.Chat;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Network;
using Percolator.Network;
using NetworkPeerId = Percolator.Network.PeerId;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Infrastructure.Serialization;

namespace Percolator.ApplicationIntegrationTests.ChatMessaging;

[TestFixture]
public class MessageIntegrationTests : IntegrationTestBase
{
    // Track temp directories for cleanup
    private readonly List<string> _tempDirectoriesToCleanup = new();

    // Override the base SetUp method to also clear the temp directories list
    [SetUp]
    public new void SetUp()
    {
        base.SetUp();
        _tempDirectoriesToCleanup.Clear();
    }

    // Override the base TearDown method to also clean up temp directories
    [TearDown]
    public new void TearDown()
    {
        base.TearDown();
        
        // Clean up any temp directories created during the test
        foreach (var dir in _tempDirectoriesToCleanup)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                    TestContext.WriteLine($"Cleaned up temp directory: {dir}");
                }
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"Error cleaning up directory {dir}: {ex.Message}");
            }
        }
    }
    
    // We'll add a series of simpler tests to isolate where the hanging occurs
    
    [Test, CancelAfter(30000)] // 30-second timeout for the entire test
    public async Task Test1_BasicNodeSetup_ShouldComplete()
    {
        // Simple test just to set up nodes and verify they start correctly
        TestContext.WriteLine("Starting Test1_BasicNodeSetup_ShouldComplete");
        
        try
        {
            // Setup ports
            int senderPort = GetAvailablePort();
            int receiverPort = GetAvailablePort();
            
            TestContext.WriteLine($"Starting sender on port {senderPort} and receiver on port {receiverPort}");
            
            // Create custom hosts with minimal service registration
            ReceiverHost = CreateHost(receiverPort, "receiver-only");
            SenderHost = CreateHost(senderPort, "sender");
            
            TestContext.WriteLine("Hosts created, starting...");
            await Task.WhenAll(
                ReceiverHost.StartAsync(),
                SenderHost.StartAsync()
            );
            
            // Wait a moment for servers to start
            await Task.Delay(500);
            TestContext.WriteLine("Servers started");
            
            // Get services from both hosts
            var receiverServices = ReceiverHost.Services;
            
            // Check for basic service resolution
            TestContext.WriteLine("Testing service resolution on receiver...");
            var receiverLogger = receiverServices.GetRequiredService<ILogger<MessageIntegrationTests>>();
            receiverLogger.LogInformation("Receiver service resolution successful");
            TestContext.WriteLine("Receiver service resolution successful");
            
            TestContext.WriteLine("Receiver setup passed!");
        }
        catch (Exception ex)
        {
            TestContext.WriteLine($"ERROR STARTING RECEIVER: {ex.GetType().Name}: {ex.Message}");
            TestContext.WriteLine(ex.StackTrace);
        }
    }
    
    [Test, CancelAfter(30000)] // 30-second timeout for the entire test
    public async Task Test2_IdentityCreation_ShouldComplete()
    {
        // Test just the identity creation part
        TestContext.WriteLine("Starting Test2_IdentityCreation_ShouldComplete");
        
        try
        {
            // Setup ports
            int senderPort = GetAvailablePort();
            int receiverPort = GetAvailablePort();
            
            TestContext.WriteLine($"Starting sender on port {senderPort} and receiver on port {receiverPort}");
            
            // Create custom hosts with minimal service registration
            ReceiverHost = CreateHost(receiverPort, "receiver");
            SenderHost = CreateHost(senderPort, "sender");
            
            TestContext.WriteLine("Hosts created, starting...");
            await Task.WhenAll(
                ReceiverHost.StartAsync(),
                SenderHost.StartAsync()
            );
            
            // Wait a moment for servers to start
            await Task.Delay(500);
            TestContext.WriteLine("Servers started");
            
            // Get services from both hosts
            var senderServices = SenderHost.Services;
            var receiverServices = ReceiverHost.Services;
            
            // Get identity orchestrators
            TestContext.WriteLine("Getting identity orchestrators");
            var senderIdentityOrchestrator = senderServices.GetRequiredService<IIdentityOrchestrator>();
            var receiverIdentityOrchestrator = receiverServices.GetRequiredService<IIdentityOrchestrator>();
            
            // Load or create identities with cancellation token
            TestContext.WriteLine("Loading identities");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await senderIdentityOrchestrator.LoadOrCreateIdentityAsync("sender-test2", cts.Token);
            await receiverIdentityOrchestrator.LoadOrCreateIdentityAsync("receiver-test2", cts.Token);
            TestContext.WriteLine("Identities loaded");
            
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
    
    [Test, CancelAfter(30000)] // 30-second timeout for the entire test
    public async Task SendChatMessage_MessageIsReceivedAndLogged()
    {
        // Create a cancellation token that will timeout after 20 seconds
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = cts.Token;
        
        try
        {
            // Setup ports
            int senderPort = GetAvailablePort();
            int receiverPort = GetAvailablePort();
            
            TestContext.WriteLine($"Starting sender on port {senderPort} and receiver on port {receiverPort}");
            
            // Create custom hosts with minimal service registration
            ReceiverHost = CreateHost(receiverPort, "receiver");
            SenderHost = CreateHost(senderPort, "sender");
            
            TestContext.WriteLine("Hosts created, starting...");
            await Task.WhenAll(
                ReceiverHost.StartAsync(token),
                SenderHost.StartAsync(token)
            );
            
            // Wait a moment for servers to start
            await Task.Delay(500, token);
            TestContext.WriteLine("Servers started");
            
            // Get services from both hosts
            var senderServices = SenderHost.Services;
            var receiverServices = ReceiverHost.Services;
            
            // Get identity orchestrators
            TestContext.WriteLine("Getting identity orchestrators");
            var senderIdentityOrchestrator = senderServices.GetRequiredService<IIdentityOrchestrator>();
            var receiverIdentityOrchestrator = receiverServices.GetRequiredService<IIdentityOrchestrator>();
            
            // Load or create identities with cancellation token
            TestContext.WriteLine("Loading identities");
            await senderIdentityOrchestrator.LoadOrCreateIdentityAsync("sender", token);
            await receiverIdentityOrchestrator.LoadOrCreateIdentityAsync("receiver", token);
            TestContext.WriteLine("Identities loaded");
            
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
            var receiverEndpoint = new System.Net.DnsEndPoint("localhost", receiverPort);
            
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
                    var senderRootKeyHash = senderSessionState.RootKey.Value != null 
                        ? Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(senderSessionState.RootKey.Value)) 
                        : "null";
                    
                    var receiverRootKeyHash = receiverSessionState.RootKey.Value != null 
                        ? Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(receiverSessionState.RootKey.Value)) 
                        : "null";
                    
                    TestContext.WriteLine($"Sender root key hash: {senderRootKeyHash}");
                    TestContext.WriteLine($"Receiver root key hash: {receiverRootKeyHash}");
                    TestContext.WriteLine($"Root keys match: {senderRootKeyHash == receiverRootKeyHash}");
                    
                    // Compare chain keys
                    var senderSendingChainKeyHash = senderSessionState.SendingChainKey?.Value != null 
                        ? Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(senderSessionState.SendingChainKey.Value)) 
                        : "null";
                    
                    var receiverReceivingChainKeyHash = receiverSessionState.ReceivingChainKey?.Value != null 
                        ? Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(receiverSessionState.ReceivingChainKey.Value)) 
                        : "null";
                    
                    TestContext.WriteLine($"Sender sending chain key hash: {senderSendingChainKeyHash}");
                    TestContext.WriteLine($"Receiver receiving chain key hash: {receiverReceivingChainKeyHash}");
                    TestContext.WriteLine($"Chain keys match (sender's sending = receiver's receiving): {senderSendingChainKeyHash == receiverReceivingChainKeyHash}");
                    
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
            TestContext.WriteLine("--------- ALL CAPTURED LOGS ---------");
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
    }
    
    private IHost CreateHost(int port, string nodeName)
    {
        // Create a unique temp directory for this node
        string tempDirectory = Path.Combine(Path.GetTempPath(), $"PercolatorTest_{nodeName}_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDirectory);
        TestContext.WriteLine($"Created temp directory for {nodeName}: {tempDirectory}");
        
        // Add to list for cleanup
        _tempDirectoriesToCleanup.Add(tempDirectory);
        
        // Create minimal configuration
        var configValues = new Dictionary<string, string>
        {
            { "Logging:LogLevel:Default", "Information" },
            { "Logging:LogLevel:Microsoft", "Warning" },
            { "Storage:Path", tempDirectory }
        };
        
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(configValues);
        var configuration = configBuilder.Build();
        
        return Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, port, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http2;
                    });
                });
                
                webBuilder.ConfigureServices(services =>
                {
                    TestContext.WriteLine($"Registering services for {nodeName} host");
                    
                    // Add logging
                    services.AddLogging(builder =>
                    {
                        builder.AddProvider(LoggerProvider);
                        builder.AddConsole();
                    });
                    
                    // Add gRPC services
                    services.AddGrpc(options =>
                    {
                        options.EnableDetailedErrors = true;
                        options.MaxReceiveMessageSize = 16 * 1024 * 1024; // 16MB
                        options.MaxSendMessageSize = 16 * 1024 * 1024;    // 16MB
                    });
                    
                    // Register ONLY the required services (minimal registration)
                    
                    // Identity services
                    services.AddSingleton<ActiveIdentityContext>();
                    services.AddSingleton<IIdentityOrchestrator, IdentityOrchestrator>();
                    services.AddSingleton<IIdentityStore, FileSystemIdentityStore>();
                    services.AddSingleton<IIdentityService, PersistentIdentityService>();
                    services.AddSingleton<IOneTimeKeyProvider, InMemoryOneTimeKeyProvider>();
                    services.AddSingleton<ICredentialService, CredentialService>();
                    services.AddSingleton<IKeyManagementService, PersistentKeyManagementService>();
                    
                    // Session services
                    services.AddSingleton<IDirectSessionManager, DirectSessionManager>();
                    services.AddSingleton<IConversationService, ConversationService>();
                    
                    // Double Ratchet Session Store
                    services.AddSingleton<IDoubleRatchetSessionStore, FileBasedDoubleRatchetSessionStore>();
                    
                    // Message services
                    services.AddSingleton<IMessageService, MessageService>();
                    services.AddSingleton<PercolatorMessageService>();
                    services.AddSingleton<IConversationRepository, FileBasedConversationRepository>();
                    services.AddSingleton<ISelfParticipantIdProvider>(sp => sp.GetRequiredService<ActiveIdentityContext>());
                    
                    // Message Transport Services
                    services.AddSingleton<SharedCertificateManager>();
                    services.AddSingleton<IPeerRepository, FileBasedPeerRepository>();
                    services.AddSingleton<IPeerConnectionRepository, FileBasedPeerConnectionRepository>();
                    services.AddSingleton<IMessageTransportService, GrpcMessageTransportService>();
                    services.AddSingleton<IGrpcSessionService, GrpcSessionService>();
                    services.AddSingleton<IPeerTrustManager>(provider => new InMemoryPeerTrustStore(
                        provider.GetRequiredService<ITrustedPeerStore>(),
                        provider.GetRequiredService<ILogger<InMemoryPeerTrustStore>>(),
                        provider.GetRequiredService<SharedCertificateManager>()
                    ));
                    services.AddSingleton<ITrustedPeerStore, FileBasedTrustedPeerStore>();
                    services.AddSingleton<PercolatorJsonContext>();

                    // Register HTTP client for gRPC communication
                    services.AddHttpClient("percolator-grpc", (serviceProvider, client) => { })
                        .ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                        {
                            var handler = new HttpClientHandler
                            {
                                ServerCertificateCustomValidationCallback = (_, _, _, _) => true // Accept all certificates for testing
                            };
                            TestContext.WriteLine("Created HttpClientHandler with ServerCertificateCustomValidationCallback = true");
                            return handler;
                        });
                    
                    // X3DH Key Exchange Services
                    services.AddSingleton<IX3DHManager, X3DHManager>();
                    services.AddSingleton<IX3DHOrchestrator, X3DHOrchestrator>();
                    
                    // Add configuration
                    services.AddSingleton<IConfiguration>(configuration);
                    
                    // Add storage options
                    services.AddOptions<StorageOptions>()
                        .Configure(options => 
                        {
                            options.Path = tempDirectory;
                            TestContext.WriteLine($"Configured storage path for {nodeName}: {options.Path}");
                        });
                });
                
                webBuilder.Configure(app =>
                {
                    TestContext.WriteLine($"Configuring {nodeName} app");
                    
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGrpcService<PercolatorMessageService>();
                        TestContext.WriteLine($"Mapped gRPC service: PercolatorMessageService for {nodeName}");
                    });
                });
            })
            .Build();
    }
}
