using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Percolator.Application.Network;
using Microsoft.AspNetCore.Routing;
using Percolator.Application;
using Percolator.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Percolator.Infrastructure.Identity;
using Percolator.Application.Identity;
using System.Threading;
using Percolator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Percolator.ApplicationIntegrationTests;

public abstract class IntegrationTestBase
{
    // Make hosts protected so derived classes can access them
    protected IHost? SenderHost { get; set; }
    protected IHost? ReceiverHost { get; set; }
    protected TestLoggerProvider LoggerProvider { get; private set; }

    [SetUp]
    public virtual async Task SetUpAsync()
    {
        TestContext.WriteLine("SetUp: Starting setup process.");
        LoggerProvider = new TestLoggerProvider();
        TestContext.WriteLine("SetUp: TestLoggerProvider created.");
        await Task.CompletedTask;
    }

    [TearDown]
    public virtual async Task TearDownAsync()
    {
        TestContext.WriteLine("TearDown: Starting teardown process.");

        if (SenderHost != null)
        {
            TestContext.WriteLine("TearDown: Stopping SenderHost...");
            await SenderHost.StopAsync();
            TestContext.WriteLine("TearDown: SenderHost stopped.");
            SenderHost.Dispose();
            TestContext.WriteLine("TearDown: SenderHost disposed.");
            SenderHost = null;
        }

        if (ReceiverHost != null)
        {
            TestContext.WriteLine("TearDown: Stopping ReceiverHost...");
            await ReceiverHost.StopAsync();
            TestContext.WriteLine("TearDown: ReceiverHost stopped.");
            ReceiverHost.Dispose();
            TestContext.WriteLine("TearDown: ReceiverHost disposed.");
            ReceiverHost = null;
        }

        if (LoggerProvider != null)
        {
            TestContext.WriteLine("TearDown: Disposing LoggerProvider.");
            LoggerProvider.Dispose();
            LoggerProvider = null;
        }
        
        TestContext.WriteLine("TearDown: Teardown process complete.");

        // Final garbage collection to release any resources
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    protected int GetAvailablePort()
    {
        // Create a socket and let the OS assign an available port
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    protected virtual IHost CreateHost(int port, string hostType, Action<IServiceCollection>? additionalServiceRegistration = null)
    {
        var hostBuilder = new HostBuilder()
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "Percolator:DataDirectoryPath", Path.Combine(Path.GetTempPath(), $"PercolatorIntegrationTest_{hostType}_{Guid.NewGuid()}") },
                    { "Percolator:LocalDevelopmentMode", "true" },
                    { "Percolator:Port", port.ToString() },
                    {"Percolator:Cryptography:EnableCryptographicMaterialLogging", "true"}
                });
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging(builder =>
                {
                    builder.AddProvider(LoggerProvider);
                    builder.AddConsole();
                    builder.AddDebug();
                });

                // Add gRPC services
                services.AddGrpc(options =>
                {
                    options.EnableDetailedErrors = true;
                    options.MaxReceiveMessageSize = 16 * 1024 * 1024; // 16MB
                    options.MaxSendMessageSize = 16 * 1024 * 1024;    // 16MB
                });

                // Register application and infrastructure services
                services.AddInfrastructureServices(context.Configuration);
                services.AddIdentityInfrastructure();
                services.AddApplicationServices(context.Configuration);
                // Add additional services if needed
                additionalServiceRegistration?.Invoke(services);
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseKestrel(options =>
                {
                    // Configure to listen on the provided port
                    options.Listen(IPAddress.Loopback, port, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http2;
                        // No TLS for local development
                    });
                })
                .Configure(app =>
                {
                    // Configure endpoints like in MessageListenerService
                    app.UseRouting();
                    
                    // Map the PercolatorMessageService to the gRPC endpoint
                    app.UseEndpoints(endpoints => {
                        endpoints.MapGrpcService<PercolatorMessageService>();
                    });
                });
            });

        return hostBuilder.Build();
    }

    protected async Task<IHost> CreateAndInitializeHostAsync(int port, string hostType, string identityName, Action<IServiceCollection>? additionalServiceRegistration = null)
    {
        var host = CreateHost(port, hostType, additionalServiceRegistration);

        // Apply EF Core migrations to ensure the database is up-to-date
        using (var scope = host.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<PercolatorDbContext>();
            await dbContext.Database.MigrateAsync();
        }

        using (var scope = host.Services.CreateScope())
        {
            var identityOrchestrator = scope.ServiceProvider.GetRequiredService<IIdentityOrchestrator>();
            await identityOrchestrator.LoadOrCreateIdentityAsync(identityName, CancellationToken.None);
        }
        return host;
    }

    protected class TestLoggerProvider : ILoggerProvider, IDisposable
    {
        private readonly ConcurrentDictionary<string, TestLogger> _loggers = new();
        private readonly ConcurrentQueue<LogEntry> _logEntries = new();
        private readonly ConcurrentBag<string> _logMessages = new();
        private bool _disposed;

        public IReadOnlyList<LogEntry> LogEntries => _logEntries.ToArray();

        public ILogger CreateLogger(string categoryName)
        {
            return _loggers.GetOrAdd(categoryName, name => new TestLogger(name, this, _logMessages));
        }

        internal void AddLogEntry(LogEntry logEntry)
        {
            _logEntries.Enqueue(logEntry);
        }

        public bool ContainsLog(string partialMessage, LogLevel? level = null)
        {
            return _logEntries.Any(entry =>
                entry.Message.Contains(partialMessage) &&
                (!level.HasValue || entry.LogLevel == level.Value));
        }

        public IEnumerable<string> GetAllLogMessages()
        {
            return _logMessages.ToList();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _loggers.Clear();
                }

                _disposed = true;
            }
        }

        private class TestLogger : ILogger
        {
            private readonly string _name;
            private readonly TestLoggerProvider _provider;
            private readonly ConcurrentBag<string> _logMessages;

            public TestLogger(string name, TestLoggerProvider provider, ConcurrentBag<string> logMessages)
            {
                _name = name;
                _provider = provider;
                _logMessages = logMessages;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);
                _provider.AddLogEntry(new LogEntry
                {
                    LogLevel = logLevel,
                    EventId = eventId,
                    Message = message,
                    Exception = exception,
                    CategoryName = _name,
                    Timestamp = DateTimeOffset.UtcNow
                });

                _logMessages.Add(message);

                // Also output to console for debugging
                Console.WriteLine($"[{logLevel}] {_name}: {message}");
                if (exception != null)
                {
                    Console.WriteLine($"Exception: {exception}");
                }
            }
        }
    }

    public class LogEntry
    {
        public LogLevel LogLevel { get; set; }
        public EventId EventId { get; set; }
        public string Message { get; set; } = string.Empty;
        public Exception? Exception { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public DateTimeOffset Timestamp { get; set; }
    }
}
