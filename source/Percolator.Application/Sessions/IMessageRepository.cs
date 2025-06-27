using Percolator.Sessions;

namespace Percolator.Application.Sessions;

/// <summary>
/// Defines a repository for storing and retrieving messages.
/// </summary>
public interface IMessageRepository
{
    /// <summary>
    /// Adds a new message to the repository.
    /// </summary>
    /// <param name="message">The message to add.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task AddAsync(DirectMessage message);
}
