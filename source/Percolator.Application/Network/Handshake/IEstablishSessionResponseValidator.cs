using Percolator.Contracts;

namespace Percolator.Application.Network.Handshake;

/// <summary>
/// Validates and parses an EstablishSessionResponse to extract session and identity information.
/// Returns null if validation fails.
/// </summary>
public interface IEstablishSessionResponseValidator
{
    /// <summary>
    /// Validates the EstablishSessionResponse and extracts session/identity information.
    /// </summary>
    /// <param name="response">The response to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Validation result with session ID and remote identity info, or null if validation fails.</returns>
    Task<EstablishSessionResponseValidationResult?> TryValidateAsync(
        EstablishSessionResponse response,
        CancellationToken cancellationToken = default);
}
