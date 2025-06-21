namespace Percolator.Cryptography
{
    public class InvalidMessageOrderException : Exception
    {
        public InvalidMessageOrderException(string message) : base(message)
        {
        }

        public InvalidMessageOrderException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
