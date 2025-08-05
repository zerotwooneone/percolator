using Microsoft.Extensions.Logging;

namespace Percolator.CryptographyTests
{
    public class NullLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
        {
            // Do nothing
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new NullLogger<object>();
        }

        public void Dispose()
        {
            // Do nothing
        }
        
        public ILogger<T> CreateLogger<T>()
        {
            return new NullLogger<T>();
        }
    }
}
