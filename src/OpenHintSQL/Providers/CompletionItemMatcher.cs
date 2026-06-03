using System;
using System.Text;

namespace OpenHintSQL.Providers
{
    internal static class CompletionItemMatcher
    {
        public static bool Matches(CompletionItemData item, string prefix)
        {
            if (item == null)
                return false;

            if (string.IsNullOrEmpty(prefix))
                return true;

            return GetRelevance(item, prefix) > 0;
        }

        public static bool MatchesName(string name, string prefix)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (string.IsNullOrEmpty(prefix))
                return true;

            return GetNameRelevance(name, prefix) > 0;
        }

        public static int GetRelevance(CompletionItemData item, string prefix)
        {
            if (item == null || string.IsNullOrEmpty(prefix))
                return 0;

            int best = 0;
            best = Math.Max(best, GetNameRelevance(item.Text, prefix));
            best = Math.Max(best, GetNameRelevance(item.InsertText, prefix));
            best = Math.Max(best, GetNameRelevance(ExtractObjectName(item.Text), prefix));
            best = Math.Max(best, GetNameRelevance(ExtractObjectName(item.InsertText), prefix));
            return best;
        }

        public static string GetSortText(CompletionItemData item)
        {
            if (item == null)
                return string.Empty;

            var objectName = ExtractObjectName(item.Text);
            if (!string.IsNullOrWhiteSpace(objectName))
                return objectName;

            return item.Text ?? string.Empty;
        }

        public static string ExtractObjectName(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            var firstToken = text.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, 2)[0];
            var lastDot = firstToken.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < firstToken.Length - 1)
                firstToken = firstToken.Substring(lastDot + 1);

            return UnquoteIdentifier(firstToken);
        }

        private static int GetNameRelevance(string name, string prefix)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(prefix))
                return 0;

            if (string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase))
                return 4;

            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return 3;

            var objectName = ExtractObjectName(name);
            if (!string.IsNullOrEmpty(objectName) &&
                !string.Equals(objectName, name, StringComparison.Ordinal))
            {
                if (string.Equals(objectName, prefix, StringComparison.OrdinalIgnoreCase))
                    return 4;

                if (objectName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return 3;
            }

            if (HasAbbreviationPrefix(objectName, prefix) ||
                HasAbbreviationPrefix(name, prefix))
            {
                return 2;
            }

            if (name.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (!string.IsNullOrEmpty(objectName) &&
                 objectName.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return 1;
            }

            return 0;
        }

        private static bool HasAbbreviationPrefix(string name, string prefix)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(prefix))
                return false;

            var abbreviation = BuildAbbreviation(UnquoteIdentifier(name));
            return abbreviation.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildAbbreviation(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            var sb = new StringBuilder();
            char previous = '\0';
            bool takeNext = true;

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (!char.IsLetterOrDigit(c))
                {
                    takeNext = true;
                    previous = c;
                    continue;
                }

                bool previousIsLetter = char.IsLetter(previous);
                bool upperAfterLower = char.IsUpper(c) && previousIsLetter && char.IsLower(previous);
                bool acronymBoundary = char.IsUpper(c) &&
                    previousIsLetter &&
                    char.IsUpper(previous) &&
                    i + 1 < name.Length &&
                    char.IsLower(name[i + 1]);
                bool digitBoundary = char.IsDigit(c) && previous != '\0' && !char.IsDigit(previous);

                if (takeNext || upperAfterLower || acronymBoundary || digitBoundary)
                    sb.Append(char.ToLowerInvariant(c));

                takeNext = false;
                previous = c;
            }

            return sb.ToString();
        }

        private static string UnquoteIdentifier(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
                return identifier;

            var result = identifier;
            if (result.Length >= 2 && result[0] == '[' && result[result.Length - 1] == ']')
                result = result.Substring(1, result.Length - 2).Replace("]]", "]");

            return result;
        }
    }
}
