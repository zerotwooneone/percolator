using Microsoft.Extensions.Logging;
using System;

namespace Percolator.CryptographyTests
{
    public class NullLogger<T> : ILogger<T>
    {
        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        
        public bool IsEnabled(LogLevel logLevel) => false;
        
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            // Do nothing - it's a null logger
        }
        
        private class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new NullScope();
            
            private NullScope() { }
            
            public void Dispose() { }
        }
    }
}
