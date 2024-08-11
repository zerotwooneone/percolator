using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Data;
using Percolator.Desktop.Main;

namespace Percolator.Desktop.Domain.Client;

public class SelfProvider : ISelfProvider, IHostedService, IPreUiInitializer
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<SelfProvider> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private SelfModel? _self;
    private readonly TaskCompletionSource _preAppComplete = new();
    public Task PreAppComplete => _preAppComplete.Task;
    
    public SelfProvider(
        ILoggerFactory loggerFactory,
        ILogger<SelfProvider> logger,
        IServiceScopeFactory serviceScopeFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = logger;
        _serviceScopeFactory = serviceScopeFactory;
    }
    
    public SelfModel GetSelf()
    {
        if (_self is null)
        {
            throw new InvalidOperationException("self has not been initialized");
        }
        return _self;
    }
    private const string KeyContainerName = "Percolator";

    public static string GetRandomNickname(int? seed=null)
    {
        var random = seed is null 
            ? new Random() 
            : new Random(seed.Value);
        var numberOfChars = random.Next(9, 20);
        var vowels = new[] {'a', 'A', '4', '@', '^', 'e', 'E', '3', 'i', 'I', '1','o', 'O', '0', 'u', 'U', 'y', 'Y'};
        var consonants = new[]{'b', 'c', 'd', 'f', 'g', 'h', 'j', 'k', 'l', 'm', 'n', 'p', 'q', 'r', 's', 't', 'v', 'w', 'x', 'z','B', 'C','(', 'D', 'F', 'G', 'H','#','8', 'J', 'K', 'L','7', 'M', 'N', 'P', 'Q', 'R', 'S','$', 'T', 'V', 'W', 'X', 'Z'};
        var list = new List<char>(numberOfChars*2);
        var isVowel = random.Next(1,1001) %2 == 0;
        do
        {
            var newPart = isVowel
                ? Enumerable.Range(0, random.Next(1, 3)).Select(_ => vowels[random.Next(0, vowels.Length)])
                : new[] {consonants[random.Next(0, consonants.Length)]};
            list.AddRange(newPart);
            isVowel = !isVowel;
        } while (list.Count < numberOfChars);

        return new string(list.ToArray());
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Start_PreUi(cancellationToken);
            _preAppComplete.SetResult();
        }
        catch (Exception e)
        {
            _preAppComplete.SetException(e);
            throw;
        }
    }

    private async Task Start_PreUi(CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var selfRows = (from s in dbContext.SelfRows
            select s).Take(2).ToArray();
        if (selfRows.Length >= 2)
        {
            _logger.LogWarning("too many self rows");
        }

        Self selfDb;
        if (selfRows.Length == 0)
        {
            var newSelf = new Self
            {
                IdentitySuffix = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            };
            dbContext.SelfRows.Add(newSelf);
            await dbContext.SaveChangesAsync(cancellationToken);
            selfDb=newSelf;
        }
        else
        {
            selfDb=selfRows[0];
        }
        
        var identitySuffix = new Guid( Convert.FromBase64String(selfDb.IdentitySuffix));
        var csp = new CspParameters
        {
            KeyContainerName = $"{KeyContainerName}.{identitySuffix}",
            Flags = 
                CspProviderFlags.UseArchivableKey | 
                CspProviderFlags.UseMachineKeyStore | 
                CspProviderFlags.UseDefaultKeyContainer
        };
        var identity = new RSACryptoServiceProvider(csp);
        var preferredNickname = selfDb.PreferredNickname ?? GetRandomNickname(ByteUtils.GetIntFromBytes(identity.ExportRSAPublicKey()));
        _self = new SelfModel(
            identitySuffix, 
            identity,
            preferredNickname,
            _loggerFactory.CreateLogger<SelfModel>());
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}