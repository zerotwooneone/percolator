using System;

namespace Pecolator.Cryptography
{
    public class InvalidMessageOrderException : Exception
    {
        public InvalidMessageOrderException(string message) : base(message)
        {
        }
    }
}
