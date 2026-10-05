using System.IO;
using System.Linq;
using System.Text;

namespace NzbDrone.Core.Download.Clients.YandexMusic
{
    internal static class PathSanitizer
    {
        private const int MaxComponentBytes = 250;

        public static string Sanitize(string component)
            => Sanitize(component, MaxComponentBytes);

        public static string Sanitize(string component, int maxBytes)
        {
            if (maxBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            if (string.IsNullOrWhiteSpace(component))
            {
                return "_";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(component.Length);
            foreach (var ch in component)
            {
                builder.Append(invalid.Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch);
            }

            var cleaned = builder.ToString().Trim('.', ' ', '_');
            if (cleaned.Length == 0)
            {
                return "_";
            }

            return TruncateToBytes(cleaned, maxBytes);
        }

        private static string TruncateToBytes(string value, int maxBytes)
        {
            var encoding = Encoding.UTF8;
            if (encoding.GetByteCount(value) <= maxBytes)
            {
                return value;
            }

            // Drop characters from the tail until the encoded form fits.
            var chars = value.ToCharArray();
            var length = chars.Length;
            while (length > 0 && encoding.GetByteCount(chars, 0, length) > maxBytes)
            {
                length--;
            }
            return new string(chars, 0, length);
        }
    }
}
