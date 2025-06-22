namespace Percolator.Application.Security
{
    /// <summary>
    /// Provides centralized methods for validating user-provided input to prevent security vulnerabilities.
    /// </summary>
    public static class InputValidator
    {
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        /// <summary>
        /// Validates that a string segment is safe to use within a file path.
        /// It checks for directory traversal characters and other invalid file system characters.
        /// </summary>
        /// <param name="pathSegment">The path segment to validate.</param>
        /// <returns>True if the segment is safe, false otherwise.</returns>
        public static bool IsValidPathSegment(string? pathSegment)
        {
            if (string.IsNullOrEmpty(pathSegment))
            {
                // An empty or null segment is considered valid (it means "no sub-path").
                return true;
            }

            // 1. Check for directory traversal sequences.
            if (pathSegment.Contains(".."))
            {
                return false;
            }

            // 2. Check for absolute path indicators.
            if (Path.IsPathRooted(pathSegment))
            {
                return false;
            }

            // 3. Check for any characters that are invalid in file or directory names.
            // This is a comprehensive check against OS-level invalid characters.
            if (pathSegment.Any(c => InvalidFileNameChars.Contains(c)))
            {
                return false;
            }

            return true;
        }
    }
}
