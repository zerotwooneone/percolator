namespace Percolator.Application.ReverseSignal;

public interface ICallbackEndpointValidator
{
    CallbackEndpointValidationResult Validate(string host, int port);
}

public readonly record struct CallbackEndpointValidationResult(
    bool IsValid,
    string? ErrorMessage,
    bool IsIpAddress,
    bool IsLanTarget);
