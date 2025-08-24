using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using System.Net;
using System.Net.Sockets;

namespace Percolator.ApplicationIntegrationTests.ChatMessaging
{
    [TestFixture]
    public class MinimalHostTest
    {
        private IHost? _host;

        [TearDown]
        public void TearDown()
        {
            _host?.Dispose();
        }

        [Test, CancelAfter(10000)] // 10-second timeout
        public async Task EmptyHost_ShouldStartAndStop()
        {
            // Create a minimal host without any Percolator services
            TestContext.WriteLine("Creating minimal host");
            
            int port = GetAvailablePort();
            TestContext.WriteLine($"Using port: {port}");
            
            _host = Host.CreateDefaultBuilder()
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.ConfigureServices(services =>
                    {
                        // No services registered
                        TestContext.WriteLine("Configuring empty services");
                    });
                    
                    webBuilder.Configure(app =>
                    {
                        // Minimal middleware
                        TestContext.WriteLine("Configuring empty app");
                        app.UseRouting();
                    });
                    
                    webBuilder.UseUrls($"http://localhost:{port}");
                })
                .Build();
            
            TestContext.WriteLine("Starting host");
            await _host.StartAsync();
            TestContext.WriteLine("Host started successfully");
            
            // Verify the host is running
            var hostServices = _host.Services;
            var env = hostServices.GetService<IWebHostEnvironment>();
            
            TestContext.WriteLine($"Host environment: {env?.EnvironmentName}");
            
            // Stop the host
            TestContext.WriteLine("Stopping host");
            await _host.StopAsync();
            TestContext.WriteLine("Host stopped successfully");
            
            Assert.Pass("Minimal host test passed");
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
    }
}
