using System.Collections.Generic;

namespace Percolator.Messaging
{
    /// <summary>
    /// Defines the limited set of allowed message annotations (emoji).
    /// </summary>
    public static class AllowedAnnotations
    {
        public const string ThumbsUp = "👍";
        public const string ThumbsDown = "👎";
        public const string Heart = "❤️";
        public const string Smile = "😄";
        public const string Tada = "🎉";

        private static readonly HashSet<string> ValidEmoji = new()
        {
            ThumbsUp,
            ThumbsDown,
            Heart,
            Smile,
            Tada
        };

        public static bool IsAllowed(string emoji)
        {
            return !string.IsNullOrEmpty(emoji) && ValidEmoji.Contains(emoji);
        }
    }
}
