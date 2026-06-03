using System;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Loads database schema metadata asynchronously using ADO.NET.
    /// Uses a single batch query with multiple result sets for efficiency.
    /// </summary>
    internal static class AsyncSchemaLoader
    {
        /// <summary>
        /// SQL batch that returns three result sets:
        ///   Result 1: Tables/Views with their columns
        ///   Result 2: Primary-key columns (one row per PK column, in key_ordinal order)
        ///   Result 3: Foreign-key columns (one row per FK column, in constraint_column_id order)
        /// </summary>
        private const string SchemaQuery = @"
-- Result 1: Tables and Views with columns
SELECT
    s.name           AS schema_name,
    o.name           AS object_name,
    o.type           AS object_type,
    c.name           AS column_name,
    tp.name          AS data_type,
    c.max_length,
    c.is_nullable,
    c.is_identity,
    c.column_id
FROM sys.objects o
INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
INNER JOIN sys.columns c ON o.object_id = c.object_id
INNER JOIN sys.types tp ON c.user_type_id = tp.user_type_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('U', 'V')
ORDER BY s.name, o.name, c.column_id;

-- Result 2: Primary-key columns
SELECT
    s.name   AS schema_name,
    t.name   AS table_name,
    c.name   AS column_name,
    ic.key_ordinal
FROM sys.indexes i
INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
INNER JOIN sys.tables t  ON i.object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
WHERE i.is_primary_key = 1
  AND t.is_ms_shipped = 0
ORDER BY s.name, t.name, ic.key_ordinal;

-- Result 3: Foreign-key columns
SELECT
    fk.name                          AS fk_name,
    fkc.constraint_column_id         AS col_ordinal,
    ps.name                          AS parent_schema,
    pt.name                          AS parent_table,
    pc.name                          AS parent_column,
    rs.name                          AS ref_schema,
    rt.name                          AS ref_table,
    rc.name                          AS ref_column
FROM sys.foreign_keys fk
INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
INNER JOIN sys.tables   pt ON fkc.parent_object_id     = pt.object_id
INNER JOIN sys.schemas  ps ON pt.schema_id             = ps.schema_id
INNER JOIN sys.columns  pc ON fkc.parent_object_id     = pc.object_id
                          AND fkc.parent_column_id     = pc.column_id
INNER JOIN sys.tables   rt ON fkc.referenced_object_id = rt.object_id
INNER JOIN sys.schemas  rs ON rt.schema_id             = rs.schema_id
INNER JOIN sys.columns  rc ON fkc.referenced_object_id = rc.object_id
                          AND fkc.referenced_column_id = rc.column_id
WHERE pt.is_ms_shipped = 0
  AND rt.is_ms_shipped = 0
ORDER BY fk.name, fkc.constraint_column_id;
";

        /// <summary>
        /// Loads the full database schema from the given connection string.
        /// </summary>
        /// <param name="connectionString">SQL Server connection string.</param>
        /// <returns>A fully populated and built <see cref="DatabaseSchema"/>.</returns>
        public static async Task<DatabaseSchema> LoadAsync(string connectionString)
        {
            var schema = new DatabaseSchema();

            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);

                    using (var command = new SqlCommand(SchemaQuery, connection))
                    {
                        command.CommandTimeout = 30;

                        using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            // ----- Result set 1: Tables and Views with columns -----
                            while (await reader.ReadAsync().ConfigureAwait(false))
                            {
                                var schemaName = reader.GetString(0);  // schema_name
                                var objectName = reader.GetString(1);  // object_name
                                var objectType = reader.GetString(2).Trim(); // 'U' or 'V'
                                var columnName = reader.GetString(3);  // column_name
                                var dataType   = reader.GetString(4);  // data_type
                                var maxLength  = reader.GetInt16(5);   // max_length
                                var isNullable = reader.GetBoolean(6); // is_nullable
                                var isIdentity = reader.GetBoolean(7); // is_identity
                                var columnId   = reader.GetInt32(8);   // column_id

                                var fullName = $"{schemaName}.{objectName}";
                                var isView = objectType == "V";
                                var targetDict = isView ? schema.Views : schema.Tables;

                                if (!targetDict.TryGetValue(fullName, out var tableInfo))
                                {
                                    tableInfo = new TableInfo
                                    {
                                        SchemaName = schemaName,
                                        Name = objectName,
                                        ObjectType = isView ? "VIEW" : "TABLE"
                                    };
                                    targetDict[fullName] = tableInfo;
                                }

                                tableInfo.Columns.Add(new ColumnInfo
                                {
                                    Name = columnName,
                                    DataType = dataType,
                                    MaxLength = maxLength,
                                    IsNullable = isNullable,
                                    IsIdentity = isIdentity,
                                    OrdinalPosition = columnId
                                });
                            }

                            // ----- Result set 2: Primary-key columns -----
                            // Tag the matching ColumnInfo (already loaded in result 1) as PK.
                            if (await reader.NextResultAsync().ConfigureAwait(false))
                            {
                                while (await reader.ReadAsync().ConfigureAwait(false))
                                {
                                    var schemaName = reader.GetString(0);
                                    var tableName  = reader.GetString(1);
                                    var columnName = reader.GetString(2);

                                    var fullName = $"{schemaName}.{tableName}";
                                    if (!schema.Tables.TryGetValue(fullName, out var tableInfo))
                                        continue;

                                    var col = tableInfo.Columns.FirstOrDefault(c =>
                                        string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
                                    if (col == null)
                                        continue;

                                    col.IsPrimaryKey = true;
                                    tableInfo.PrimaryKeyColumns.Add(col);
                                }
                            }

                            // ----- Result set 3: Foreign-key columns -----
                            // Stage raw rows on DatabaseSchema; resolved into object refs by Build().
                            if (await reader.NextResultAsync().ConfigureAwait(false))
                            {
                                while (await reader.ReadAsync().ConfigureAwait(false))
                                {
                                    schema.PendingForeignKeys.Add(new PendingFkRow
                                    {
                                        Name             = reader.GetString(0),
                                        Ordinal          = reader.GetInt32(1),
                                        ParentSchema     = reader.GetString(2),
                                        ParentTable      = reader.GetString(3),
                                        ParentColumn     = reader.GetString(4),
                                        ReferencedSchema = reader.GetString(5),
                                        ReferencedTable  = reader.GetString(6),
                                        ReferencedColumn = reader.GetString(7)
                                    });
                                }
                            }
                        }
                    }
                }

                schema.IsLoaded = true;
                schema.LoadedAt = DateTime.UtcNow;
                schema.Build();

                int fkCount = schema.Tables.Values.Sum(t => t.ForeignKeys.Count);
                Logger.Log($"Schema loaded: {schema.Tables.Count} tables, " +
                           $"{schema.Views.Count} views, " +
                           $"{fkCount} foreign keys");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load database schema", ex);
                schema.LoadError = ex.Message;
                // Return whatever we managed to load (possibly empty)
            }

            return schema;
        }
    }
}
