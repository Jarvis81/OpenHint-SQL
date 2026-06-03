using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Persists <see cref="DatabaseSchema"/> snapshots to disk so SSMS restarts don't
    /// have to re-query the server. Cache files live in
    /// <c>%LocalAppData%\OpenHintSQL\schemacache\</c>, keyed by a hash of
    /// <c>server|database</c> to avoid path-illegal characters and case mismatches.
    /// </summary>
    internal static class SchemaPersister
    {
        /// <summary>
        /// How long a persisted snapshot is considered fresh. Older snapshots are
        /// ignored — the caller re-queries the server and overwrites the file.
        /// </summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

        private static bool DiskCacheDisabled =>
            Logger.IsEnvironmentFlagEnabled("OPENHINTSQL_DISABLE_DISK_CACHE");

        /// <summary>
        /// Properties we must NOT serialise: cyclic (FK refs back to TableInfo) or derived
        /// (rebuilt by <see cref="DatabaseSchema.Build"/> from other persisted fields).
        ///
        /// IMPORTANT: this list lives here in <see cref="SchemaPersister"/> rather than as
        /// <c>[JsonIgnore]</c> attributes on the POCOs. Type-level Newtonsoft attributes are
        /// walked during early MEF / VSPackage type discovery in SSMS — if Newtonsoft.Json
        /// can't be resolved at that moment (it's in a non-root probe path on SSMS 20), the
        /// whole DLL fails to load and the extension silently vanishes (no Output pane, no
        /// popup). Keeping Newtonsoft usage to method bodies (this file) avoids that trap.
        /// </summary>
        private static readonly HashSet<string> _ignoredProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            // TableInfo
            nameof(TableInfo.PrimaryKeyColumns),
            nameof(TableInfo.ForeignKeys),
            nameof(TableInfo.IncomingForeignKeys),
            // DatabaseSchema
            nameof(DatabaseSchema.AllTableNames),
            nameof(DatabaseSchema.AllViewNames),
            nameof(DatabaseSchema.Procedures),
        };

        private static readonly JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            Formatting = Formatting.None,
            ContractResolver = new IgnoreNamedPropertiesResolver(_ignoredProperties),
        };

        /// <summary>
        /// Skips a fixed set of property names during (de)serialisation. Used in place of
        /// <c>[JsonIgnore]</c> attributes to keep Newtonsoft.Json out of the assembly's
        /// type metadata.
        /// </summary>
        private sealed class IgnoreNamedPropertiesResolver : DefaultContractResolver
        {
            private readonly HashSet<string> _ignored;
            public IgnoreNamedPropertiesResolver(HashSet<string> ignored) { _ignored = ignored; }

            protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
            {
                var prop = base.CreateProperty(member, memberSerialization);
                if (_ignored.Contains(member.Name))
                {
                    prop.ShouldSerialize = _ => false;
                    prop.ShouldDeserialize = _ => false;
                    prop.Ignored = true;
                }
                return prop;
            }
        }

        /// <summary>
        /// Directory that holds all cache files. Created on demand.
        /// </summary>
        private static string CacheDir
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, "OpenHintSQL", "schemacache");
            }
        }

        /// <summary>
        /// Attempts to load a cached schema for the given <paramref name="cacheKey"/>.
        /// Returns null if no file exists, the file is older than <see cref="MaxAge"/>,
        /// or deserialisation fails. Never throws.
        /// </summary>
        public static async Task<DatabaseSchema> TryLoadAsync(string cacheKey)
        {
            if (DiskCacheDisabled)
                return null;

            try
            {
                var path = GetPath(cacheKey);
                if (!File.Exists(path))
                    return null;

                var info = new FileInfo(path);
                var age = DateTime.UtcNow - info.LastWriteTimeUtc;
                if (age > MaxAge)
                {
                    Logger.Diagnostic($"SchemaPersister: cache for [{cacheKey}] is stale ({age.TotalHours:F1}h), ignoring");
                    return null;
                }

                string json;
                using (var reader = new StreamReader(path))
                {
                    json = await reader.ReadToEndAsync().ConfigureAwait(false);
                }

                var schema = JsonConvert.DeserializeObject<DatabaseSchema>(json, _jsonSettings);
                if (schema == null)
                    return null;

                schema.Build();
                schema.IsLoaded = true;
                // LoadedAt comes back from JSON — keep it so SchemaCache TTL maths still work.

                Logger.Diagnostic($"SchemaPersister: loaded [{cacheKey}] from disk " +
                           $"({schema.Tables.Count} tables, age {age.TotalHours:F1}h)");
                return schema;
            }
            catch (Exception ex)
            {
                Logger.Warn($"SchemaPersister.TryLoadAsync failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Persists <paramref name="schema"/> to disk under <paramref name="cacheKey"/>.
        /// Writes atomically (to a .tmp file then moves) so a partial write can never
        /// corrupt the cache. Never throws.
        /// </summary>
        public static async Task TrySaveAsync(string cacheKey, DatabaseSchema schema)
        {
            if (schema == null || !schema.IsLoaded)
                return;
            if (DiskCacheDisabled)
                return;

            try
            {
                Directory.CreateDirectory(CacheDir);

                var path = GetPath(cacheKey);
                var tmpPath = path + ".tmp";

                var json = JsonConvert.SerializeObject(schema, _jsonSettings);

                using (var writer = new StreamWriter(tmpPath, append: false, encoding: Encoding.UTF8))
                {
                    await writer.WriteAsync(json).ConfigureAwait(false);
                }

                // Atomic replace: File.Move with overwrite=true keeps the cache file
                // valid even if the process is killed between the two operations.
                if (File.Exists(path))
                    File.Delete(path);
                File.Move(tmpPath, path);

                Logger.Diagnostic($"SchemaPersister: saved [{cacheKey}] to disk ({schema.Tables.Count} tables)");
            }
            catch (Exception ex)
            {
                Logger.Warn($"SchemaPersister.TrySaveAsync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Deletes the persisted cache for <paramref name="cacheKey"/>, forcing the next
        /// load to hit the server. Called by manual-refresh paths.
        /// </summary>
        public static void Invalidate(string cacheKey)
        {
            try
            {
                var path = GetPath(cacheKey);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Logger.Diagnostic($"SchemaPersister: invalidated [{cacheKey}]");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"SchemaPersister.Invalidate failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Maps a cache key like <c>"MYSERVER\SQLEXPRESS|AdventureWorks"</c> to a safe
        /// filename. We hash the key so backslashes / case differences / very long
        /// names don't escape the cache directory.
        /// </summary>
        private static string GetPath(string cacheKey)
        {
            // 12 lowercase hex chars is plenty to avoid collisions across one user's
            // connection list while keeping filenames short and easy to eyeball.
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(cacheKey.ToLowerInvariant()));
                var sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++)
                    sb.Append(bytes[i].ToString("x2"));
                return Path.Combine(CacheDir, sb.ToString() + ".json");
            }
        }
    }
}
