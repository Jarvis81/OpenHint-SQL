using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenHintSQL.ResultGridScripting
{
    /// <summary>
    /// Maps the SSMS result-grid view of a cell ("NULL" literal vs typed string)
    /// to a stable enum the script builder uses for both CREATE column type and
    /// VALUE literal emission.
    /// </summary>
    internal enum InferredSqlType
    {
        NVarCharMax,
        BigInt,
        Decimal,
        Bit,
        UniqueIdentifier,
        DateTime2
    }

    /// <summary>
    /// Heuristic SQL type inference from string values. The SSMS result grid
    /// hands us cell data only as <see cref="string"/> — original column types
    /// (and any joined/expression columns) are unreachable. We sniff per column
    /// by walking values and falling back the moment one disagrees.
    ///
    /// Priority (each requires all non-null values to match):
    ///   BIT &gt; BIGINT &gt; DECIMAL &gt; UNIQUEIDENTIFIER &gt; DATETIME2 &gt; NVARCHAR(MAX)
    ///
    /// All-null columns default to NVARCHAR(MAX) — safest, since user can
    /// always edit the CREATE TABLE if they know better.
    /// </summary>
    internal static class SqlTypeInferer
    {
        private const string NullLiteral = "NULL";

        private static readonly Regex GuidRegex = new Regex(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

        // SSMS renders datetimes as ISO-ish: "2026-06-04 12:34:56.7890000" or
        // "2026-06-04" for DATE. Accept either with optional fractional seconds.
        private static readonly Regex IsoDateTimeRegex = new Regex(
            @"^\d{4}-\d{2}-\d{2}([ T]\d{2}:\d{2}:\d{2}(\.\d{1,7})?)?$",
            RegexOptions.Compiled);

        /// <summary>
        /// Returns true if the SSMS grid value should be treated as SQL NULL.
        /// </summary>
        public static bool IsNullLiteral(string value)
            => value != null && value.Equals(NullLiteral, StringComparison.Ordinal);

        public static InferredSqlType Infer(string[,] values, int columnIndex)
        {
            long rowCount = values.GetLongLength(0);
            if (rowCount == 0)
                return InferredSqlType.NVarCharMax;

            bool allNull = true;
            bool bitOk = true;
            bool bigIntOk = true;
            bool decimalOk = true;
            bool guidOk = true;
            bool dateTimeOk = true;

            for (long r = 0; r < rowCount; r++)
            {
                string v = values[r, columnIndex];
                if (IsNullLiteral(v)) continue;
                allNull = false;
                if (v == null) v = string.Empty;

                if (bitOk && !(v == "0" || v == "1"))
                    bitOk = false;
                if (bigIntOk && !long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    bigIntOk = false;
                if (decimalOk && !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                    decimalOk = false;
                if (guidOk && !GuidRegex.IsMatch(v))
                    guidOk = false;
                if (dateTimeOk && !IsoDateTimeRegex.IsMatch(v))
                    dateTimeOk = false;

                if (!bitOk && !bigIntOk && !decimalOk && !guidOk && !dateTimeOk)
                    return InferredSqlType.NVarCharMax;
            }

            if (allNull)             return InferredSqlType.NVarCharMax;
            if (bitOk)               return InferredSqlType.Bit;
            if (bigIntOk)            return InferredSqlType.BigInt;
            if (decimalOk)           return InferredSqlType.Decimal;
            if (guidOk)              return InferredSqlType.UniqueIdentifier;
            if (dateTimeOk)          return InferredSqlType.DateTime2;
            return InferredSqlType.NVarCharMax;
        }

        public static string SqlTypeDeclaration(InferredSqlType type)
        {
            switch (type)
            {
                case InferredSqlType.BigInt:           return "BIGINT";
                case InferredSqlType.Decimal:          return "DECIMAL(38, 10)";
                case InferredSqlType.Bit:              return "BIT";
                case InferredSqlType.UniqueIdentifier: return "UNIQUEIDENTIFIER";
                case InferredSqlType.DateTime2:        return "DATETIME2";
                default:                               return "NVARCHAR(MAX)";
            }
        }

        /// <summary>
        /// Emits the SQL literal for a single cell value. Uses the inferred
        /// column type to decide whether to quote, prefix with N, escape
        /// apostrophes, or pass numeric values straight through.
        /// </summary>
        public static string EmitLiteral(string value, InferredSqlType type)
        {
            if (IsNullLiteral(value))
                return "NULL";

            string v = value ?? string.Empty;

            switch (type)
            {
                case InferredSqlType.BigInt:
                case InferredSqlType.Decimal:
                case InferredSqlType.Bit:
                    return v.Length == 0 ? "NULL" : v;

                case InferredSqlType.UniqueIdentifier:
                    return v.Length == 0 ? "NULL" : "'" + v.Replace("'", "''") + "'";

                case InferredSqlType.DateTime2:
                    return v.Length == 0 ? "NULL" : "'" + v.Replace("'", "''") + "'";

                default:
                    return "N'" + v.Replace("'", "''") + "'";
            }
        }

        /// <summary>
        /// Escapes a SQL bracketed identifier — wraps with [] and doubles any
        /// embedded ] to ]] so headers containing spaces, dashes, or brackets
        /// stay valid in CREATE TABLE and INSERT column lists.
        /// </summary>
        public static string QuoteIdentifier(string identifier)
        {
            var sb = new StringBuilder(identifier.Length + 4);
            sb.Append('[');
            foreach (char ch in identifier)
            {
                if (ch == ']')
                    sb.Append(']');
                sb.Append(ch);
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
