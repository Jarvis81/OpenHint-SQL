using System;
using System.Data.SqlClient;
using System.Threading.Tasks;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Schema
{
    internal static class AsyncProcedureLoader
    {
        private const string ProcedureQuery = @"
SELECT
    s.name      AS schema_name,
    o.name      AS object_name,
    o.type_desc AS type_desc
FROM sys.objects o
INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE o.is_ms_shipped = 0
  AND o.type IN ('P', 'FN', 'IF', 'TF')
ORDER BY s.name, o.name;";

        public static async Task<ProcedureList> LoadAsync(string connectionString)
        {
            var procedures = new ProcedureList();

            try
            {
                using (var connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync().ConfigureAwait(false);

                    using (var command = new SqlCommand(ProcedureQuery, connection))
                    {
                        command.CommandTimeout = 30;

                        using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync().ConfigureAwait(false))
                            {
                                procedures.Items.Add(new ProcedureInfo
                                {
                                    SchemaName = reader.GetString(0),
                                    Name = reader.GetString(1),
                                    ObjectType = reader.GetString(2)
                                });
                            }
                        }
                    }
                }

                procedures.IsLoaded = true;
                procedures.LoadedAt = DateTime.UtcNow;
                Logger.Log($"Procedure list loaded: {procedures.Items.Count} procedure/function object(s)");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load procedure list", ex);
                procedures.LoadError = ex.Message;
            }

            return procedures;
        }
    }
}
