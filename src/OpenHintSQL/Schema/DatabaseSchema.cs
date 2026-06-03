using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using OpenHintSQL.Providers;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Raw FK row staged by the schema loader for resolution in <see cref="DatabaseSchema.Build"/>.
    /// Multi-column FKs produce multiple rows sharing the same <see cref="Name"/>.
    /// </summary>
    internal class PendingFkRow
    {
        public string Name;
        public int Ordinal;
        public string ParentSchema;
        public string ParentTable;
        public string ParentColumn;
        public string ReferencedSchema;
        public string ReferencedTable;
        public string ReferencedColumn;
    }

    /// <summary>
    /// In-memory container for a database's schema: tables, views, procedures, and functions.
    /// After populating the dictionaries, call <see cref="Build"/> to construct
    /// the sorted name lists and trie index for fast prefix search.
    /// </summary>
    internal class DatabaseSchema
    {
        /// <summary>
        /// Tables keyed by full name (schema.name), case-insensitive.
        /// </summary>
        public Dictionary<string, TableInfo> Tables { get; }
            = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Views keyed by full name (schema.name), case-insensitive.
        /// </summary>
        public Dictionary<string, TableInfo> Views { get; }
            = new Dictionary<string, TableInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stored procedures and functions keyed by full name (schema.name), case-insensitive.
        /// </summary>
        public Dictionary<string, ProcedureInfo> Procedures { get; }
            = new Dictionary<string, ProcedureInfo>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Sorted list of all table full names, built by <see cref="Build"/>.
        /// Excluded from JSON by <c>SchemaPersister</c>'s contract resolver.
        /// </summary>
        public IReadOnlyList<string> AllTableNames { get; private set; }
            = Array.Empty<string>();

        /// <summary>
        /// Sorted list of all view full names, built by <see cref="Build"/>.
        /// </summary>
        public IReadOnlyList<string> AllViewNames { get; private set; }
            = Array.Empty<string>();

        /// <summary>
        /// Whether the schema has been loaded from the database.
        /// </summary>
        public bool IsLoaded { get; set; }

        /// <summary>
        /// UTC timestamp when this schema was loaded.
        /// </summary>
        public DateTime LoadedAt { get; set; }

        /// <summary>
        /// Last load error for an unloaded schema, if a live server query failed.
        /// </summary>
        public string LoadError { get; set; }

        /// <summary>
        /// Trie index for fast prefix search across all object names (tables, views, procs).
        /// </summary>
        private readonly TrieIndex<CompletionItemData> _trieIndex = new TrieIndex<CompletionItemData>();

        /// <summary>
        /// Raw FK rows populated by <see cref="AsyncSchemaLoader"/> before <see cref="Build"/> resolves them
        /// into <see cref="ForeignKeyInfo"/> object refs on the appropriate <see cref="TableInfo"/>.
        /// </summary>
        internal List<PendingFkRow> PendingForeignKeys { get; } = new List<PendingFkRow>();

        /// <summary>
        /// Returns an empty, unloaded schema instance.
        /// </summary>
        public static DatabaseSchema Empty
        {
            get
            {
                return new DatabaseSchema { IsLoaded = false };
            }
        }

        /// <summary>
        /// Builds the sorted name lists and trie index from the populated dictionaries.
        /// Must be called after all tables, views, and procedures have been added — and
        /// after deserialising from disk, since none of the derived state is persisted.
        /// </summary>
        public void Build()
        {
            // Build sorted table name list
            var tableNames = Tables.Keys.ToList();
            tableNames.Sort(StringComparer.OrdinalIgnoreCase);
            AllTableNames = new ReadOnlyCollection<string>(tableNames);

            // Build sorted view name list
            var viewNames = Views.Keys.ToList();
            viewNames.Sort(StringComparer.OrdinalIgnoreCase);
            AllViewNames = new ReadOnlyCollection<string>(viewNames);

            // Rebuild PrimaryKeyColumns from the IsPrimaryKey flag on each column.
            // (The list itself isn't persisted — it's a convenience derived from columns.)
            foreach (var table in Tables.Values)
            {
                table.PrimaryKeyColumns.Clear();
                foreach (var col in table.Columns)
                {
                    if (col.IsPrimaryKey)
                        table.PrimaryKeyColumns.Add(col);
                }
                // Also wipe stale FK refs from a previous Build() — they'll be re-resolved below.
                table.ForeignKeys.Clear();
                table.IncomingForeignKeys.Clear();
            }

            // Build trie index from all objects
            _trieIndex.Clear();

            foreach (var kvp in Tables)
            {
                var t = kvp.Value;
                var item = new CompletionItemData
                {
                    Text = t.FullName,
                    InsertText = t.BracketedName,
                    Description = $"Table: {t.BracketedName}",
                    Kind = CompletionItemKind.Table,
                    Priority = 10,
                    IconKey = "Table"
                };
                // Index by full name and by short name for flexible matching
                _trieIndex.Insert(t.FullName, item);
                _trieIndex.Insert(t.Name, item);
            }

            foreach (var kvp in Views)
            {
                var v = kvp.Value;
                var item = new CompletionItemData
                {
                    Text = v.FullName,
                    InsertText = v.BracketedName,
                    Description = $"View: {v.BracketedName}",
                    Kind = CompletionItemKind.View,
                    Priority = 10,
                    IconKey = "View"
                };
                _trieIndex.Insert(v.FullName, item);
                _trieIndex.Insert(v.Name, item);
            }

            foreach (var kvp in Procedures)
            {
                var p = kvp.Value;
                var kind = p.ObjectType != null && p.ObjectType.Contains("FUNCTION")
                    ? CompletionItemKind.Function
                    : CompletionItemKind.Procedure;
                var label = kind == CompletionItemKind.Function ? "Function" : "Procedure";

                var item = new CompletionItemData
                {
                    Text = p.FullName,
                    InsertText = p.Name,
                    Description = $"{label}: {p.FullName}",
                    Kind = kind,
                    Priority = 20,
                    IconKey = label
                };
                _trieIndex.Insert(p.FullName, item);
                _trieIndex.Insert(p.Name, item);
            }

            // Resolve pending FK rows into ForeignKeyInfo refs on the appropriate tables.
            // Rows arrive in (fk_name, constraint_column_id) order; group consecutive rows by name.
            ResolveForeignKeys();
        }

        /// <summary>
        /// Walks <see cref="PendingForeignKeys"/> and builds <see cref="ForeignKeyInfo"/> objects
        /// wired into the parent and referenced <see cref="TableInfo"/> instances.
        /// </summary>
        private void ResolveForeignKeys()
        {
            ForeignKeyInfo current = null;
            string currentName = null;
            int resolved = 0, skipped = 0;

            foreach (var row in PendingForeignKeys)
            {
                if (row.Name != currentName)
                {
                    // Commit the previous FK (if any) and start a new one.
                    if (current != null)
                        AttachForeignKey(current);

                    currentName = row.Name;
                    current = StartForeignKey(row);
                    if (current == null)
                    {
                        skipped++;
                        continue;
                    }
                    resolved++;
                }

                if (current == null)
                    continue;

                var parentCol = FindColumn(current.ParentTable, row.ParentColumn);
                var refCol = FindColumn(current.ReferencedTable, row.ReferencedColumn);
                if (parentCol != null && refCol != null)
                {
                    current.ParentColumns.Add(parentCol);
                    current.ReferencedColumns.Add(refCol);
                }
            }

            if (current != null)
                AttachForeignKey(current);

            if (skipped > 0)
                Logger.Warn($"Skipped {skipped} FKs that could not be resolved to known tables");

            // NOTE: PendingForeignKeys is intentionally NOT cleared — it's the only
            // string-keyed FK snapshot that survives JSON persistence. Build() re-runs
            // after deserialise and needs these rows again.
        }

        private ForeignKeyInfo StartForeignKey(PendingFkRow row)
        {
            var parentKey = $"{row.ParentSchema}.{row.ParentTable}";
            var refKey = $"{row.ReferencedSchema}.{row.ReferencedTable}";

            if (!Tables.TryGetValue(parentKey, out var parent))
                return null;
            if (!Tables.TryGetValue(refKey, out var referenced))
                return null;

            return new ForeignKeyInfo
            {
                Name = row.Name,
                ParentTable = parent,
                ReferencedTable = referenced
            };
        }

        private static void AttachForeignKey(ForeignKeyInfo fk)
        {
            if (fk.ParentColumns.Count == 0 || fk.ReferencedColumns.Count == 0)
                return;

            fk.ParentTable.ForeignKeys.Add(fk);
            fk.ReferencedTable.IncomingForeignKeys.Add(fk);
        }

        private static ColumnInfo FindColumn(TableInfo table, string columnName)
        {
            if (table == null || string.IsNullOrEmpty(columnName))
                return null;
            return table.Columns.FirstOrDefault(c =>
                string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Returns FK constraints that join <paramref name="a"/> and <paramref name="b"/> in either direction.
        /// Used by the JOIN-suggestion code path to decide which tables are "FK-related" to scoped tables.
        /// </summary>
        public IEnumerable<ForeignKeyInfo> GetForeignKeysBetween(TableInfo a, TableInfo b)
        {
            if (a == null || b == null)
                yield break;

            foreach (var fk in a.ForeignKeys)
            {
                if (ReferenceEquals(fk.ReferencedTable, b))
                    yield return fk;
            }
            foreach (var fk in a.IncomingForeignKeys)
            {
                if (ReferenceEquals(fk.ParentTable, b))
                    yield return fk;
            }
        }

        /// <summary>
        /// Returns completion items for all objects whose names start with the given prefix.
        /// Searches across tables, views, and procedures/functions via the trie index.
        /// </summary>
        /// <param name="prefix">The prefix to match (case-insensitive).</param>
        /// <returns>List of matching <see cref="CompletionItemData"/> items.</returns>
        public List<CompletionItemData> GetMatchingObjects(string prefix)
        {
            if (string.IsNullOrEmpty(prefix) || !IsLoaded)
                return new List<CompletionItemData>();

            return _trieIndex.Search(prefix);
        }

        /// <summary>
        /// Returns completion items for all columns of the specified table or view.
        /// Accepts either the full name (schema.name) or short name.
        /// </summary>
        /// <param name="tableName">Table or view name to look up (case-insensitive).</param>
        /// <returns>List of column <see cref="CompletionItemData"/> items, or empty if not found.</returns>
        public List<CompletionItemData> GetColumnsForTable(string tableName)
        {
            var results = new List<CompletionItemData>();
            if (string.IsNullOrEmpty(tableName) || !IsLoaded)
                return results;

            // Try tables first, then views
            TableInfo tableInfo = null;
            if (!Tables.TryGetValue(tableName, out tableInfo))
            {
                Views.TryGetValue(tableName, out tableInfo);
            }

            // Fallback: search by short name if full name didn't match
            if (tableInfo == null)
            {
                tableInfo = Tables.Values.FirstOrDefault(t =>
                    t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
            }
            if (tableInfo == null)
            {
                tableInfo = Views.Values.FirstOrDefault(v =>
                    v.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));
            }

            if (tableInfo == null)
                return results;

            foreach (var col in tableInfo.Columns)
            {
                results.Add(new CompletionItemData
                {
                    Text = col.Name,
                    InsertText = col.Name,
                    Description = col.DisplayText,
                    Kind = CompletionItemKind.Column,
                    Priority = 5,
                    IconKey = "Column"
                });
            }

            return results;
        }
    }
}
