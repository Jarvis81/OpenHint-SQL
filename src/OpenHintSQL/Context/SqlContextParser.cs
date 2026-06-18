using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace OpenHintSQL.Context
{
    /// <summary>
    /// Represents the syntactic context at the current caret position within a
    /// T-SQL statement. Used to decide which category of completions to offer.
    /// </summary>
    public enum SqlContext
    {
        /// <summary>No specific context detected.</summary>
        Unknown,
        /// <summary>Inside the column list of a SELECT clause.</summary>
        SelectClause,
        /// <summary>After UPDATE — expecting a target table name.</summary>
        UpdateTarget,
        /// <summary>Inside a FROM clause — expecting table/view names.</summary>
        FromClause,
        /// <summary>Inside a WHERE clause — expecting columns/expressions.</summary>
        WhereClause,
        /// <summary>Inside a JOIN clause — expecting table names.</summary>
        JoinClause,
        /// <summary>Inside an UPDATE … SET clause — expecting column = value.</summary>
        SetClause,
        /// <summary>Inside the column list of an INSERT statement.</summary>
        InsertColumns,
        /// <summary>Inside an ORDER BY clause.</summary>
        OrderByClause,
        /// <summary>Inside a GROUP BY clause.</summary>
        GroupByClause,
        /// <summary>Inside a HAVING clause.</summary>
        HavingClause,
        /// <summary>Inside a JOIN … ON condition.</summary>
        OnClause,
        /// <summary>Inside a CREATE TABLE statement.</summary>
        CreateTable,
        /// <summary>Inside an ALTER TABLE statement.</summary>
        AlterTable,
        /// <summary>After EXEC/EXECUTE — expecting procedure name.</summary>
        Exec,
        /// <summary>After sp_help - expecting a table name.</summary>
        SpHelp,
        /// <summary>After sp_helptext - expecting a view/procedure/function name.</summary>
        SpHelpText,
        /// <summary>After USE - expecting a database name.</summary>
        UseDatabase,
        /// <summary>After DECLARE — expecting variable declaration.</summary>
        DeclareVariable,
        /// <summary>At the top level, not inside any particular clause.</summary>
        TopLevel
    }

    /// <summary>
    /// Result returned by <see cref="SqlContextParser.GetTableContext"/> when the
    /// caret is positioned after a table alias and a dot separator (e.g. "u.").
    /// </summary>
    public class TableContextResult
    {
        /// <summary>The alias or table name before the dot.</summary>
        public string AliasOrTable { get; set; }

        /// <summary>
        /// Resolved table name if the alias was found in a FROM/JOIN clause.
        /// Null if no resolution was possible.
        /// </summary>
        public string ResolvedTable { get; set; }

        /// <summary>
        /// Resolved schema name, if the table reference included a schema prefix
        /// (e.g. "dbo.Users"). Null if none was found.
        /// </summary>
        public string SchemaName { get; set; }
    }

    /// <summary>
    /// Lightweight heuristic SQL context analyser. Scans backward from the caret
    /// position to determine the current clause context, the word being typed, and
    /// table alias resolution.
    /// <para>
    /// This is intentionally <b>not</b> a full SQL parser — it uses simple keyword
    /// matching and parenthesis counting to give a "good enough" answer quickly.
    /// </para>
    /// </summary>
    internal static class SqlContextParser
    {
        // ──────────────────────────────────────────────
        //  Keyword → context mappings (ordered by scan priority)
        // ──────────────────────────────────────────────

        private static readonly (string keyword, SqlContext context)[] ClauseKeywords =
        {
            // Multi-word keywords first so they match before their single-word prefix
            ("ORDER BY",   SqlContext.OrderByClause),
            ("GROUP BY",   SqlContext.GroupByClause),
            ("INNER JOIN", SqlContext.JoinClause),
            ("LEFT JOIN",  SqlContext.JoinClause),
            ("RIGHT JOIN", SqlContext.JoinClause),
            ("FULL JOIN",  SqlContext.JoinClause),
            ("CROSS JOIN", SqlContext.JoinClause),
            ("FULL OUTER JOIN",  SqlContext.JoinClause),
            ("LEFT OUTER JOIN",  SqlContext.JoinClause),
            ("RIGHT OUTER JOIN", SqlContext.JoinClause),
            ("CROSS APPLY", SqlContext.JoinClause),
            ("OUTER APPLY", SqlContext.JoinClause),
            ("CREATE TABLE", SqlContext.CreateTable),
            ("ALTER TABLE",  SqlContext.AlterTable),
            ("INSERT INTO",  SqlContext.InsertColumns),
            ("DELETE FROM",  SqlContext.UpdateTarget),   // DELETE FROM <table> — no alias
            ("SP_HELPTEXT",  SqlContext.SpHelpText),
            ("SP_HELP",      SqlContext.SpHelp),

            // Single-word keywords
            ("SELECT",  SqlContext.SelectClause),
            ("UPDATE",  SqlContext.UpdateTarget),
            ("FROM",    SqlContext.FromClause),
            ("WHERE",   SqlContext.WhereClause),
            ("JOIN",    SqlContext.JoinClause),
            ("HAVING",  SqlContext.HavingClause),
            ("SET",     SqlContext.SetClause),
            ("ON",      SqlContext.OnClause),
            ("EXEC",    SqlContext.Exec),
            ("EXECUTE", SqlContext.Exec),
            ("USE",     SqlContext.UseDatabase),
            ("DECLARE", SqlContext.DeclareVariable),
        };

        /// <summary>Characters that can form a SQL identifier or keyword.</summary>
        private static bool IsWordChar(char c)
            => char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#';

        // ──────────────────────────────────────────────
        //  GetWordBeforeCaret
        // ──────────────────────────────────────────────

        /// <summary>
        /// Extracts the word (identifier fragment) that the user is currently typing,
        /// ending at <paramref name="caretOffset"/>. Returns empty string if the caret
        /// is not preceded by word characters.
        /// </summary>
        /// <param name="text">Full editor text.</param>
        /// <param name="caretOffset">Zero-based caret position within the text.</param>
        public static string GetWordBeforeCaret(string text, int caretOffset)
        {
            if (string.IsNullOrEmpty(text) || caretOffset <= 0)
                return string.Empty;

            int end = Math.Min(caretOffset, text.Length);
            int start = end;

            while (start > 0 && IsWordChar(text[start - 1]))
                start--;

            return start < end ? text.Substring(start, end - start) : string.Empty;
        }

        // ──────────────────────────────────────────────
        //  GetContext
        // ──────────────────────────────────────────────

        /// <summary>
        /// Determines the <see cref="SqlContext"/> at <paramref name="caretOffset"/>
        /// by scanning backward for the nearest unmatched clause keyword.
        /// </summary>
        /// <param name="text">Full editor text.</param>
        /// <param name="caretOffset">Zero-based caret position within the text.</param>
        public static SqlContext GetContext(string text, int caretOffset)
        {
            if (string.IsNullOrEmpty(text) || caretOffset <= 0)
                return SqlContext.TopLevel;

            int limit = Math.Min(caretOffset, text.Length);

            // Take the portion of text before the caret, normalise whitespace for
            // easier keyword matching, but preserve parenthesis depth so we can
            // ignore subqueries.
            string before = text.Substring(0, limit);

            // Walk backward through the text tracking parenthesis nesting depth.
            // We only care about keywords at the *current* nesting level (depth 0).
            int parenDepth = 0;

            for (int pos = before.Length - 1; pos >= 0; pos--)
            {
                char c = before[pos];

                if (c == ')')
                {
                    parenDepth++;
                    continue;
                }
                if (c == '(')
                {
                    if (parenDepth > 0) parenDepth--;
                    continue;
                }

                // Only test keywords when we're at the outermost level
                if (parenDepth > 0)
                    continue;

                // Optimisation: only test when we're at a word boundary
                if (!char.IsLetter(c))
                    continue;

                // Try to match each clause keyword at this position
                foreach (var (keyword, context) in ClauseKeywords)
                {
                    if (MatchKeywordBackward(before, pos, keyword))
                        return context;
                }
            }

            return SqlContext.TopLevel;
        }

        // ──────────────────────────────────────────────
        //  TryGetClauseAtCaret
        // ──────────────────────────────────────────────

        /// <summary>
        /// Returns true when the text immediately before the caret is whitespace and
        /// the token preceding that whitespace is a clause keyword (FROM, JOIN, etc.).
        /// Used to decide whether typing a space should auto-pop the completion list.
        /// </summary>
        /// <param name="text">Full editor text.</param>
        /// <param name="caretOffset">Zero-based caret position within the text.</param>
        /// <param name="context">Set to the matched <see cref="SqlContext"/> on success.</param>
        public static bool TryGetClauseAtCaret(string text, int caretOffset, out SqlContext context)
        {
            context = SqlContext.Unknown;

            if (string.IsNullOrEmpty(text) || caretOffset <= 0)
                return false;

            int limit = Math.Min(caretOffset, text.Length);

            // Walk back across one-or-more whitespace chars immediately before the caret.
            int pos = limit - 1;
            if (pos < 0 || !char.IsWhiteSpace(text[pos]))
                return false;

            while (pos >= 0 && char.IsWhiteSpace(text[pos]))
                pos--;

            if (pos < 0)
                return false;

            // pos now points at the last char of the preceding token. Try matching each
            // clause keyword ending exactly here.
            foreach (var (keyword, ctx) in ClauseKeywords)
            {
                if (MatchKeywordBackward(text, pos, keyword))
                {
                    context = ctx;
                    return true;
                }
            }

            return false;
        }

        // ──────────────────────────────────────────────
        //  GetTableContext
        // ──────────────────────────────────────────────

        /// <summary>
        /// If the caret is immediately after an alias/table name followed by a dot
        /// (e.g. "u." or "dbo."), returns a <see cref="TableContextResult"/> with the
        /// alias and, where possible, the resolved table name from FROM/JOIN clauses.
        /// Returns <c>null</c> if there is no dot context.
        /// </summary>
        /// <param name="text">Full editor text.</param>
        /// <param name="caretOffset">Zero-based caret position within the text.</param>
        public static TableContextResult GetTableContext(string text, int caretOffset)
        {
            if (string.IsNullOrEmpty(text) || caretOffset < 2)
                return null;

            int limit = Math.Min(caretOffset, text.Length);

            // The character immediately before the caret (or before the word being
            // typed) should be a dot.
            int dotPos = -1;

            // If the user is mid-word after a dot (e.g. "u.Na|"), walk back past the
            // partial word to find the dot.
            int scanPos = limit - 1;
            while (scanPos >= 0 && IsWordChar(text[scanPos]))
                scanPos--;

            if (scanPos >= 0 && text[scanPos] == '.')
                dotPos = scanPos;
            else if (limit > 0 && text[limit - 1] == '.')
                dotPos = limit - 1;

            if (dotPos < 1)
                return null;

            // Extract the word before the dot
            int aliasEnd = dotPos;
            int aliasStart = aliasEnd - 1;
            while (aliasStart > 0 && IsWordChar(text[aliasStart - 1]))
                aliasStart--;

            if (aliasStart >= aliasEnd)
                return null;

            // Handle bracket-delimited identifiers: [schema].table
            if (aliasStart > 0 && aliasEnd > 0)
            {
                int bracketEnd = dotPos - 1;
                if (bracketEnd >= 0 && text[bracketEnd] == ']')
                {
                    int bracketStart = text.LastIndexOf('[', bracketEnd);
                    if (bracketStart >= 0)
                    {
                        aliasStart = bracketStart + 1;
                        aliasEnd = bracketEnd;
                    }
                }
            }

            string alias = text.Substring(aliasStart, aliasEnd - aliasStart).Trim();
            if (string.IsNullOrEmpty(alias))
                return null;

            // Attempt to resolve the alias by scanning FROM / JOIN clauses
            var resolved = ResolveAlias(text, limit, alias);

            return new TableContextResult
            {
                AliasOrTable = alias,
                ResolvedTable = resolved.tableName,
                SchemaName = resolved.schemaName
            };
        }

        // ──────────────────────────────────────────────
        //  Private helpers
        // ──────────────────────────────────────────────

        /// <summary>
        /// Tests whether the keyword ends at position <paramref name="endPos"/>
        /// (inclusive) in <paramref name="text"/>, with word boundaries on both sides.
        /// Comparison is case-insensitive.
        /// </summary>
        private static bool MatchKeywordBackward(string text, int endPos, string keyword)
        {
            int kwLen = keyword.Length;
            int startPos = endPos - kwLen + 1;

            if (startPos < 0)
                return false;

            // Word boundary before the keyword
            if (startPos > 0 && IsWordChar(text[startPos - 1]))
                return false;

            // Word boundary after the keyword
            if (endPos + 1 < text.Length && IsWordChar(text[endPos + 1]))
                return false;

            // Case-insensitive compare
            for (int i = 0; i < kwLen; i++)
            {
                char tc = text[startPos + i];
                char kc = keyword[i];

                // The keyword contains spaces (e.g. "ORDER BY") — allow any
                // whitespace character in the text to match a space in the keyword.
                if (kc == ' ')
                {
                    if (!char.IsWhiteSpace(tc))
                        return false;
                }
                else
                {
                    if (char.ToUpperInvariant(tc) != char.ToUpperInvariant(kc))
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Regex pattern for extracting table references from FROM/JOIN clauses.
        /// Captures: optional [schema.] table [alias]
        /// </summary>
        private static readonly Regex TableRefPattern = new Regex(
            @"(?:FROM|JOIN)\s+" +
            @"(?:\[?(\w+)\]?\.)?" +                 // optional schema
            @"\[?(\w+)\]?" +                         // table name
            @"(?:\s+(?:AS\s+)?(\w+))?",              // optional alias
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Scans the text up to <paramref name="limit"/> for FROM/JOIN clauses and
        /// tries to resolve <paramref name="alias"/> to a table (and optionally schema).
        /// </summary>
        private static (string tableName, string schemaName) ResolveAlias(
            string text, int limit, string alias)
        {
            string searchText = text.Substring(0, limit);
            var matches = TableRefPattern.Matches(searchText);

            // Walk matches in reverse so the closest (most relevant) definition wins
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var m = matches[i];
                string schema    = m.Groups[1].Success ? m.Groups[1].Value : null;
                string tableName = m.Groups[2].Value;
                string tableAlias = m.Groups[3].Success ? m.Groups[3].Value : null;

                // Alias match — e.g. FROM Users u  → alias "u" resolves to "Users"
                if (tableAlias != null &&
                    tableAlias.Equals(alias, StringComparison.OrdinalIgnoreCase))
                {
                    return (tableName, schema);
                }

                // Direct table name match — e.g. Users.Id  (no alias, using table name)
                if (tableName.Equals(alias, StringComparison.OrdinalIgnoreCase))
                {
                    return (tableName, schema);
                }
            }

            // Could not resolve — the alias itself might be a schema name
            return (null, null);
        }
    }
}
