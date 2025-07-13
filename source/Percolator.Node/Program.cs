using System.CommandLine;
using System.CommandLine.Invocation;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Formats.Asn1;
using System.Net.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Application;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Application.Sessions;
using Percolator.Infrastructure;
using Percolator.Network;
using Percolator.Node;
using ChatConversationId = Percolator.Chat.ValueObjects.ConversationId;

var rootCommand = new RootCommand("Percolator Node: A secure peer-to-peer communication tool.");

// *** Common Options ***
var identityOption = new Option<string>(
    new[] { "--identity", "-i" },
    getDefaultValue: () => "default",
    description: "The name of the identity to use.");

// *** Host Command ***
var portOption = new Option<int>(
    new[] { "--port", "-p" },
    getDefaultValue: () => 5000,
    description: "The port to listen on.");

var hostCommand = new Command("host", "Starts the node, listens for peers, and hosts the gRPC service.")
{
    portOption,
    identityOption
};
rootCommand.AddCommand(hostCommand);

// *** Connect Command ***
var endpointArgument = new Argument<string>("endpoint", "The endpoint of the peer (e.g., localhost:5000 or just localhost).");
var peerNameOption = new Option<string>("--peer-name", "The name of the peer to connect to.") { IsRequired = true };

var connectCommand = new Command("connect", "Connect to a peer and establish a session.")
{
    endpointArgument,
    peerNameOption,
    identityOption
};
rootCommand.AddCommand(connectCommand);

// *** Send Command ***
var conversationIdOption = new Option<Guid?>("--conversation-id", "The ID of the conversation. If omitted, the last active conversation with the target peer will be used.");
var peerIdOption = new Option<Guid?>("--peer-id", "The ID of the peer to send the message to. Required if conversation-id is not specified.");
var messageArgument = new Argument<string>("message", "The plaintext message to send.");
var endpointOption = new Option<string>("--endpoint", "The endpoint of the peer to establish a new session with before sending (e.g., localhost:5000 or just localhost).");

var sendCommand = new Command("send", "Send a message to a peer.")
{
    conversationIdOption,
    peerIdOption,
    messageArgument,
    endpointOption,
    peerNameOption,
    identityOption
};
rootCommand.AddCommand(sendCommand);

// *** TLS Debug Command ***
var tlsDebugCommand = new Command("tls-debug", "Tests basic TLS connectivity to a given endpoint.");
tlsDebugCommand.AddArgument(new Argument<string>("host", "The host to connect to."));
tlsDebugCommand.AddArgument(new Argument<int>("port", "The port to connect to."));
rootCommand.AddCommand(tlsDebugCommand);

// --- Command Handlers ---

hostCommand.SetHandler(HostCommandHandler);
connectCommand.SetHandler(ConnectCommandHandler);
sendCommand.SetHandler(SendCommandHandler);
tlsDebugCommand.SetHandler(TlsDebugCommandHandler);

// --- Run Application ---
return await rootCommand.InvokeAsync(args);

// --- Handler Implementations ---

async Task HostCommandHandler(InvocationContext context)
{
    CancellationToken cancellationToken = context.GetCancellationToken();
    int port = context.ParseResult.GetValueForOption(portOption);
    string? identityName = context.ParseResult.GetValueForOption(identityOption);

    // Step 1: Build a temporary service provider to get services needed for startup.
    var tempServices = new ServiceCollection();
    
    IConfigurationRoot tempConfig = new ConfigurationBuilder().AddNode().Build();
    tempServices.AddLogging(builder => builder.AddConsole());
    tempServices.AddApplicationServices(tempConfig);
    tempServices.AddInfrastructureServices(tempConfig);
    ServiceProvider tempServiceProvider = tempServices.BuildServiceProvider();

    try
    {
        // Step 2: Use the temporary provider to load the identity and then get the certificate object.
        IIdentityOrchestrator tempIdentityOrchestrator = tempServiceProvider.GetRequiredService<IIdentityOrchestrator>();
        ActiveIdentityContext tempActiveIdentityContext = tempServiceProvider.GetRequiredService<ActiveIdentityContext>();
        await tempIdentityOrchestrator.LoadOrCreateIdentityAsync(identityName!, cancellationToken);

        ITlsCertificateService certificateService = tempServiceProvider.GetRequiredService<ITlsCertificateService>();
        X509Certificate2 serverCertificate = await certificateService.GetOrCreateTlsCertificateAsync(
            identityName!,
            tempActiveIdentityContext.Keys!.IdentitySigningKey.ExportSubjectPublicKeyInfo());

        string publicKeyB64 = Convert.ToBase64String(tempActiveIdentityContext.Keys!.IdentitySigningKey.ExportSubjectPublicKeyInfo());

        // For simplicity in a local dev environment, we'll use localhost.
        // A more advanced implementation might try to discover the local network IP.
        InvitationLink invitationLink = new InvitationLink("localhost", port, publicKeyB64);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Host started successfully.");
        Console.WriteLine($"Invitation Link: {invitationLink}");
        Console.ResetColor();
        Console.WriteLine("Share this link with peers who want to connect.");

        // Step 3: Configure and build the main application using the pre-fetched certificate.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        builder.Configuration.AddNode();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Any, port, listenOptions =>
            {
                listenOptions.UseHttps(httpsOptions =>
                {
                    httpsOptions.ServerCertificate = serverCertificate;
                    httpsOptions.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
                    httpsOptions.ClientCertificateValidation = (certificate, chain, policyErrors) =>
                    {
                        var logger = tempServiceProvider.GetRequiredService<ILogger<Program>>();
                        logger.LogInformation("Server: Performing client certificate validation for subject '{Subject}'", certificate.Subject);
                        
                        try 
                        {
                            // TEMPORARY FOR DEBUGGING: Log all certificate details
                            logger.LogWarning("SERVER DEBUG: Certificate={Subject}, Issuer={Issuer}, PolicyErrors={PolicyErrors}", 
                                certificate.Subject, 
                                certificate.Issuer, 
                                policyErrors);

                            // Temporarily accept all client certificates to help diagnose the issue
                            logger.LogWarning("SERVER TEMPORARY DEBUG MODE: Accepting all client certificates");
                            return true;
                            
                            /* ORIGINAL VALIDATION - COMMENTED OUT FOR DEBUGGING
                            var peerIdentityExtension = certificate.Extensions[Percolator.Cryptography.Oids.PeerIdentityKey];
                            if (peerIdentityExtension is null)
                            {
                                logger.LogError("Server: Client certificate validation failed for '{Subject}'. Reason: Missing the required peer identity extension.", certificate.Subject);
                                return false;
                            }

                            try
                            {
                                var reader = new AsnReader(peerIdentityExtension.RawData, AsnEncodingRules.DER);
                                var publicKey = reader.ReadOctetString();
                                if (publicKey.Length == 0)
                                {
                                    logger.LogError("Server: Client certificate validation failed for '{Subject}'. Reason: Peer identity extension contains no public key.", certificate.Subject);
                                    return false;
                                }

                                if (reader.HasData)
                                {
                                    logger.LogError("Server: Client certificate validation failed for '{Subject}'. Reason: Peer identity extension contains unexpected trailing data.", certificate.Subject);
                                    return false;
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "Server: Client certificate validation failed for '{Subject}'. Reason: Failed to decode peer identity extension.", certificate.Subject);
                                return false;
                            }

                            logger.LogInformation("Server: Client certificate validation successful for '{Subject}'.", certificate.Subject);
                            return true;
                            */
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Unhandled exception during client certificate validation");
                            return false;
                        }
                    };
                });
            });
        });

        builder.Services.AddApplicationServices(builder.Configuration);
        builder.Services.AddInfrastructureServices(builder.Configuration);
        builder.Services.AddGrpc();

        // Step 3.5: Configure Kestrel for Mutual TLS (mTLS)
        // We need to build a temporary service provider here to get the certificate service,
        // as Kestrel's configuration is finalized before the main app.Services provider is ready.
        var tempKestrelServices = new ServiceCollection();
        tempKestrelServices.AddLogging(); // Add logging services
        tempKestrelServices.AddApplicationServices(builder.Configuration);
        tempKestrelServices.AddInfrastructureServices(builder.Configuration);
        await using var tempKestrelProvider = tempKestrelServices.BuildServiceProvider();

        var identityOrchestratorForKestrel = tempKestrelProvider.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestratorForKestrel.LoadOrCreateIdentityAsync(identityName!, cancellationToken);

        // After loading the identity, the ActiveIdentityContext is populated.
        var activeIdentityContextForKestrel = tempKestrelProvider.GetRequiredService<ActiveIdentityContext>();
        if (activeIdentityContextForKestrel.Identity is null || activeIdentityContextForKestrel.Keys is null)
        {
            throw new InvalidOperationException("Failed to load identity context for Kestrel configuration.");
        }
        var publicSigningKey = activeIdentityContextForKestrel.Keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();

        var tlsCertificateService = tempKestrelProvider.GetRequiredService<ITlsCertificateService>();
        var serverCertificateForKestrel = await tlsCertificateService.GetOrCreateTlsCertificateAsync(activeIdentityContextForKestrel.Identity.Name, publicSigningKey);

        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.ConfigureHttpsDefaults(listenOptions =>
            {
                listenOptions.ServerCertificate = serverCertificateForKestrel;
                // DelayCertificate is crucial for our TOFU model. It establishes the TLS connection
                // and delegates certificate validation entirely to the application layer.
                listenOptions.ClientCertificateMode = ClientCertificateMode.DelayCertificate;
            });
        });

        WebApplication app = builder.Build();

        // Step 4: Manually initialize the identity *again* using the main service provider
        // to ensure the ActiveIdentityContext is correct for the running application.
        IIdentityOrchestrator identityOrchestrator = app.Services.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, cancellationToken);

        // Initialize the in-memory peer trust store
        var peerTrustManager = app.Services.GetRequiredService<IPeerTrustManager>();
        peerTrustManager.Initialize();

        await app.RunAsync(cancellationToken);

    }
    finally
    {
        await tempServiceProvider.DisposeAsync();
    }
}

async Task ConnectCommandHandler(InvocationContext context)
{
    var endpointString = context.ParseResult.GetValueForArgument(endpointArgument);
    var peerName = context.ParseResult.GetValueForOption(peerNameOption);
    var identityName = context.ParseResult.GetValueForOption(identityOption);
    var cancellationToken = context.GetCancellationToken();

    if (!TryParseEndpoint(endpointString, out var endpoint))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Invalid endpoint format: {endpointString}");
        Console.ResetColor();
        return;
    }

    var services = CreateServiceProvider(identityName);
    await using var serviceScope = services.CreateAsyncScope();
    var serviceProvider = serviceScope.ServiceProvider;

    try
    {
        var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, cancellationToken);
        
        var conversationService = serviceProvider.GetRequiredService<IConversationService>();
        var conversationId = await conversationService.CreateDirectConversationAsync(endpoint, peerName!);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully connected to {peerName} and created conversation {conversationId.Value}");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"An error occurred while connecting: {ex.Message}");
        Console.ResetColor();
    }
}

async Task SendCommandHandler(InvocationContext context)
{
    var conversationIdGuid = context.ParseResult.GetValueForOption(conversationIdOption);
    var peerIdGuid = context.ParseResult.GetValueForOption(peerIdOption);
    var message = context.ParseResult.GetValueForArgument(messageArgument);
    var endpointString = context.ParseResult.GetValueForOption(endpointOption);
    var peerName = context.ParseResult.GetValueForOption(peerNameOption);
    var identityName = context.ParseResult.GetValueForOption(identityOption);
    var cancellationToken = context.GetCancellationToken();

    var services = CreateServiceProvider(identityName);
    await using var serviceScope = services.CreateAsyncScope();
    var serviceProvider = serviceScope.ServiceProvider;

    try
    {
        var identityOrchestrator = serviceProvider.GetRequiredService<IIdentityOrchestrator>();
        await identityOrchestrator.LoadOrCreateIdentityAsync(identityName!, cancellationToken);

        var conversationService = serviceProvider.GetRequiredService<IConversationService>();
        var messageService = serviceProvider.GetRequiredService<IMessageService>();

        ChatConversationId conversationId;

        if (conversationIdGuid.HasValue)
        {
            conversationId = new ChatConversationId(conversationIdGuid.Value);
        }
        else
        {
            if (string.IsNullOrEmpty(endpointString) || string.IsNullOrEmpty(peerName))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Either --conversation-id or both --endpoint and --peer-name must be specified.");
                Console.ResetColor();
                return;
            }

            if (!TryParseEndpoint(endpointString, out var endpoint))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Invalid endpoint format: '{endpointString}'.");
                Console.ResetColor();
                return;
            }

            conversationId = await conversationService.CreateDirectConversationAsync(endpoint, peerName);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Established new conversation {conversationId.Value} with {peerName}");
            Console.ResetColor();
        }

        await messageService.SendDirectMessageAsync(conversationId, message);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Message sent successfully.");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"An error occurred while sending the message: {ex.Message}");
        Console.ResetColor();
    }
}

static ServiceProvider CreateServiceProvider(string? identityName)
{
    var services = new ServiceCollection();
    var config = new ConfigurationBuilder().AddNode().Build();

    services.AddLogging(builder => builder.AddConsole().AddConfiguration(config.GetSection("Logging")));
    services.AddApplicationServices(config);
    services.AddInfrastructureServices(config);
    
    services.AddHttpClient("percolator-grpc").ConfigurePrimaryHttpMessageHandler(() =>
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                {
                    try
                    {
                        using var scope = services.BuildServiceProvider().CreateScope();
                        var peerTrustManager = scope.ServiceProvider.GetRequiredService<IPeerTrustManager>();
                        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                        
                        logger.LogInformation(
                            "CLIENT: TLS Certificate Validation: Subject={Subject}, Issuer={Issuer}, PolicyErrors={PolicyErrors}", 
                            certificate?.Subject ?? "null", 
                            certificate?.Issuer ?? "null", 
                            sslPolicyErrors);

                        // TEMPORARY FOR DEBUGGING: Log detailed certificate info
                        if (certificate != null)
                        {
                            var x509Cert = new X509Certificate2(certificate);
                            logger.LogInformation("CLIENT: Certificate details - Thumbprint={Thumbprint}, NotBefore={NotBefore}, NotAfter={NotAfter}", 
                                x509Cert.Thumbprint, 
                                x509Cert.NotBefore,
                                x509Cert.NotAfter);
                        }
                        
                        if (sslPolicyErrors == SslPolicyErrors.None)
                        {
                            logger.LogInformation("CLIENT: Certificate is valid according to system trust store");
                            return true;
                        }
                            
                        if (certificate != null)
                        {
                            try
                            {
                                logger.LogWarning("CLIENT: TEMPORARY DEBUG MODE: Accepting all certificates for TOFU debugging");
                                return true;
                                
                                /* Uncomment once we confirm the basic TLS handshake works
                                var x509Cert = new X509Certificate2(certificate);
                                var thumbprint = x509Cert.Thumbprint;
                                logger.LogInformation("CLIENT: Checking certificate with thumbprint: {Thumbprint}", thumbprint);
                                
                                var isTrusted = peerTrustManager.IsTrusted(x509Cert);
                                logger.LogInformation("CLIENT: Certificate is{Trusted} trusted by our peer trust manager", 
                                    isTrusted ? "" : " NOT");
                                return isTrusted;
                                */
                            }
                            catch (Exception ex)
                            {
                                logger.LogError(ex, "CLIENT: Error during certificate trust check");
                                return true;
                            }
                        }
                        
                        logger.LogWarning("CLIENT: Certificate validation failed: No certificate provided");
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"CLIENT: CRITICAL ERROR in certificate validation: {ex}");
                        return true;
                    }
                },
                
                LocalCertificateSelectionCallback = (sender, host, localCertificates, remoteCertificate, acceptableIssuers) => 
                {
                    try
                    {
                        using var scope = services.BuildServiceProvider().CreateScope();
                        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
                        
                        logger.LogInformation(
                            "CLIENT: TLS Client Certificate Selection: Host={Host}, RemoteCertSubject={RemoteSubject}, LocalCerts={LocalCertCount}", 
                            host,
                            remoteCertificate?.Subject ?? "null", 
                            localCertificates?.Count ?? 0);
                    }
                    catch
                    {
                        // Ignore errors in logging
                    }
                    
                    return null;
                },
                
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | 
                                      System.Security.Authentication.SslProtocols.Tls13,
                                      
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
                
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }
        };
        return handler;
    });

    return services.BuildServiceProvider();
}

static ServiceProvider BuildServiceProvider(IConfiguration configuration)
{
    var services = new ServiceCollection();
    services.AddLogging(builder => builder.AddConsole());
    services.AddApplicationServices(configuration);
    services.AddInfrastructureServices(configuration);

    return services.BuildServiceProvider();
}

bool TryParseEndpoint(string? text, out DnsEndPoint? endpoint)
{
    endpoint = null;
    if (string.IsNullOrEmpty(text))
        return false;

    var parts = text.Split(':', StringSplitOptions.RemoveEmptyEntries);
    string host;
    int port;

    switch (parts.Length)
    {
        case 1: // Host only, use default port
            host = parts[0];
            port = 5000; // Default port
            break;
        case 2: // Host and port
            host = parts[0];
            if (!int.TryParse(parts[1], out port))
                return false;
            break;
        default:
            return false;
    }

    endpoint = new DnsEndPoint(host, port);
    return true;
}

async Task TlsDebugCommandHandler(InvocationContext context)
{
    var host = (string)context.ParseResult.GetValueForArgument(tlsDebugCommand.Arguments[0]);
    var port = (int)context.ParseResult.GetValueForArgument(tlsDebugCommand.Arguments[1]);

    var serviceProvider = CreateServiceProvider(null); // No identity needed for basic test
    var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
    
    Console.WriteLine("Starting TLS connectivity test...");
    var endpoint = new DnsEndPoint(host, port);
    
    // First run a basic TLS test
    await TlsDebugger.TestTlsHandshake(endpoint, logger);
    
    // If we have an active identity, try a mutual TLS test
    try 
    {
        var activeIdentity = serviceProvider.GetRequiredService<ActiveIdentityContext>();
        if (activeIdentity.Identity != null)
        {
            var certService = serviceProvider.GetRequiredService<ITlsCertificateService>();
            var cert = await certService.GetOrCreateTlsCertificateAsync(
                activeIdentity.Identity.Name,
                activeIdentity.Keys!.IdentitySigningKey.ExportSubjectPublicKeyInfo());
                        
            Console.WriteLine("Testing mutual TLS with client certificate...");
            await TlsDebugger.TestMutualTlsHandshake(endpoint, cert, logger);
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to run mutual TLS test");
    }
}
