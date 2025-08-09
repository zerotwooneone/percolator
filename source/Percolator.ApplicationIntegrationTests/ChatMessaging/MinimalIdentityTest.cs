using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using Percolator.Application.Identity;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Infrastructure;
using Percolator.Infrastructure.Identity;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Percolator.ApplicationIntegrationTests.ChatMessaging
{
    [TestFixture]
    public class MinimalIdentityTest
    {
        [Test, CancelAfter(10000)] // 10-second timeout
        public async Task MinimalIdentityCreation_ShouldComplete()
        {
            // Create a host with only the essential identity services
            TestContext.WriteLine("Creating minimal identity-enabled host");
            
            int port = GetAvailablePort();
            TestContext.WriteLine($"Using port: {port}");
            
            // Create a minimal configuration
            var configDictionary = new Dictionary<string, string?>
            {
                { "Identity:StoragePath", "TestIdentities" },
                { "Logging:LogLevel:Default", "Debug" }
            };
            
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(configDictionary)
                .Build();
                
            TestContext.WriteLine("Created minimal configuration");
            
            using var host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                    // Add a test logger provider
                    var testLoggerProvider = new TestLoggerProvider();
                    logging.AddProvider(testLoggerProvider);
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.ConfigureServices(services =>
                    {
                        TestContext.WriteLine("Registering minimal identity services");
                        
                        // Register ONLY the required services for identity loading
                        // Manually register required services instead of using AddApplicationServices
                        // to avoid registering PeerDiscoveryHostedService
                        
                        // Identity services - using the actual implementations from the Identity namespace
                        services.AddSingleton<ActiveIdentityContext>();
                        services.AddSingleton<IIdentityOrchestrator, IdentityOrchestrator>();
                        services.AddSingleton<IIdentityStore, FileSystemIdentityStore>();
                        services.AddSingleton<IIdentityService, PersistentIdentityService>();
                        services.AddSingleton<IOneTimeKeyProvider, InMemoryOneTimeKeyProvider>();
                        services.AddSingleton<ICredentialService, CredentialService>();
                        services.AddSingleton<IKeyManagementService, PersistentKeyManagementService>();
                        
                        // Add configuration
                        services.AddSingleton<IConfiguration>(configuration);
                        
                        // Add storage options required by PersistentKeyManagementService
                        services.AddOptions<StorageOptions>()
                            .Configure(options => 
                            {
                                options.Path = Path.Combine(Path.GetTempPath(), "PercolatorTest", Guid.NewGuid().ToString());
                                TestContext.WriteLine($"Using temporary storage path: {options.Path}");
                                Directory.CreateDirectory(options.Path);
                            });
                        
                        TestContext.WriteLine("Minimal identity services registered");
                    });
                    
                    webBuilder.Configure(app =>
                    {
                        // Minimal middleware
                        TestContext.WriteLine("Configuring app");
                        app.UseRouting();
                    });
                    
                    webBuilder.UseUrls($"http://localhost:{port}");
                })
                .Build();
            
            TestContext.WriteLine("Starting host");
            await host.StartAsync();
            TestContext.WriteLine("Host started successfully");
            
            // Test identity creation with a short timeout
            try
            {
                TestContext.WriteLine("Getting identity orchestrator");
                var identityOrchestrator = host.Services.GetRequiredService<IIdentityOrchestrator>();
                TestContext.WriteLine("Identity orchestrator retrieved");
                
                // Create a cancellation token with a short timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                
                TestContext.WriteLine("Loading or creating identity");
                await identityOrchestrator.LoadOrCreateIdentityAsync("test-identity", cts.Token);
                TestContext.WriteLine("Identity created successfully");
                
                // Verify that the identity is accessible through the context
                var identityContext = host.Services.GetRequiredService<ActiveIdentityContext>();
                TestContext.WriteLine($"Identity ID: {identityContext.Identity?.Id}");
                
                Assert.That(identityContext.Identity, Is.Not.Null, "Identity should be loaded in the context");
            }
            catch (Exception ex)
            {
                TestContext.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
                TestContext.WriteLine(ex.StackTrace);
                throw;
            }
        }
        
        // Helper method to get an available port
        private static int GetAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
        
        // Simple test logger provider for diagnostics
        private class TestLoggerProvider : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName)
            {
                return new TestLogger(categoryName);
            }

            public void Dispose() { }

            private class TestLogger : ILogger
            {
                private readonly string _categoryName;

                public TestLogger(string categoryName)
                {
                    _categoryName = categoryName;
                }

                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    var message = formatter(state, exception);
                    TestContext.WriteLine($"[{logLevel}] {_categoryName}: {message}");
                    
                    if (exception != null)
                    {
                        TestContext.WriteLine($"Exception: {exception}");
                    }
                }
            }
        }
    }
}
