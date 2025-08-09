using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Percolator.Cryptography;

namespace Percolator.Application.Cryptography;

/// <summary>
/// Validates cryptographic options and logs warnings for insecure configurations.
/// </summary>
public class CryptographyOptionsValidator : IValidateOptions<CryptographyOptions>
{
    private readonly ILogger<CryptographyOptionsValidator> _logger;

    public CryptographyOptionsValidator(ILogger<CryptographyOptionsValidator> logger)
    {
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, CryptographyOptions options)
    {
        if (options.EnableCryptographicMaterialLogging)
        {
            _logger.LogWarning("SECURITY WARNING: Cryptographic material hash logging is ENABLED. This should only be used for debugging.");
        }
        return ValidateOptionsResult.Success;
    }
}
