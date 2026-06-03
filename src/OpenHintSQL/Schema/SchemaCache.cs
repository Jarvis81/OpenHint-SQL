using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Schema
{
    /// <summary>
    /// Thread-safe, non-blocking cache for database schemas.
    /// Returns cached schemas immediately or <see cref="DatabaseSchema.Empty"/>
    /// while a background load is in progress (never blocks the UI thread).
    /// </summary>
    internal static class SchemaCache
    {
        /// <summary>Cache TTL — schemas older than this are considered stale.</summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(15);

        /// <summary>Cached schemas keyed by "server|database|connection-fingerprint".</summary>
        private static readonly ConcurrentDictionary<string, DatabaseSchema> _cache
            = new ConcurrentDictionary<string, DatabaseSchema>(StringComparer.OrdinalIgnoreCase);

        /// <summary>In-flight loading tasks for deduplication — prevents multiple simultaneous loads for the same key.</summary>
        private static readonly ConcurrentDictionary<string, Task> _loadingTasks
            = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, SchemaLoadFailure> _loadFailures
            = new ConcurrentDictionary<string, SchemaLoadFailure>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Raised when a schema finishes loading in the background.
        /// Parameters: cache key (server|database|connection-fingerprint), loaded schema.
        /// </summary>
        public static event Action<string, DatabaseSchema> OnSchemaLoaded;

        public static event Action<string, string> OnSchemaLoadFailed;

        private sealed class SchemaLoadFailure
        {
            public string Message { get; set; }
            public DateTime FailedAt { get; set; }
        }

        /// <summary>
        /// Gets the cached schema for the given server/database, or returns
        /// <see cref="DatabaseSchema.Empty"/> and fires a background load.
        /// This method NEVER blocks.
        /// </summary>
        /// <param name="server">SQL Server instance name.</param>
        /// <param name="database">Database name.</param>
        /// <param name="connectionString">Connection string for loading.</param>
        /// <returns>The cached schema, or Empty if not yet loaded.</returns>
        public static DatabaseSchema GetOrLoad(string server, string database, string connectionString)
        {
            var key = BuildKey(server, database, connectionString);

            // Check for a valid cached entry
            if (_cache.TryGetValue(key, out var cached) && cached.IsLoaded)
            {
                // Check TTL
                if ((DateTime.UtcNow - cached.LoadedAt) < CacheTtl)
                {
                    return cached;
                }

                // Stale — trigger background refresh but still return stale data
                Logger.Diagnostic($"Schema stale for [{key}], triggering background refresh");
                StartBackgroundLoad(key, connectionString, allowDiskCache: false);
                return cached;
            }

            // Cache miss — fire background load and return Empty immediately
            if (TryGetRecentFailure(key, out _))
                return DatabaseSchema.Empty;

            Logger.Diagnostic($"Schema cache miss for [{key}], starting background load");
            StartBackgroundLoad(key, connectionString, allowDiskCache: true);
            return DatabaseSchema.Empty;
        }

        /// <summary>
        /// Forces a refresh of the schema for the given server/database. Invalidates both
        /// the in-memory and on-disk caches so the next load is a live server query.
        /// </summary>
        public static Task RefreshAsync(string server, string database, string connectionString)
        {
            var key = BuildKey(server, database, connectionString);
            Logger.Diagnostic($"Manual schema refresh requested for [{key}]");

            _cache.TryRemove(key, out _);
            _loadFailures.TryRemove(key, out _);
            SchemaPersister.Invalidate(key);

            return LoadSchemaAsync(key, connectionString, allowDiskCache: false);
        }

        /// <summary>
        /// Clears all cached schemas and cancels any in-flight information.
        /// </summary>
        public static void Clear()
        {
            _cache.Clear();
            _loadingTasks.Clear();
            _loadFailures.Clear();
            Logger.Log("Schema cache cleared");
        }

        public static string GetLastLoadError(string server, string database, string connectionString)
        {
            var key = BuildKey(server, database, connectionString);
            return TryGetRecentFailure(key, out var failure) ? failure.Message : null;
        }

        /// <summary>
        /// Starts a background schema load if one isn't already in progress for this key.
        /// </summary>
        private static void StartBackgroundLoad(string key, string connectionString, bool allowDiskCache)
        {
            // Use GetOrAdd to ensure only one load per key
            _loadingTasks.GetOrAdd(key, k =>
            {
                _loadFailures.TryRemove(k, out _);

                return Task.Run(async () =>
                {
                    try
                    {
                        await LoadSchemaAsync(k, connectionString, allowDiskCache).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Background schema load failed", ex);
                        RecordLoadFailure(k, ex.Message);
                    }
                    finally
                    {
                        // Remove from in-flight tracking so future requests can retry
                        _loadingTasks.TryRemove(k, out _);
                    }
                });
            });
        }

        /// <summary>
        /// Performs the actual async schema load. Tries the on-disk persister first;
        /// only falls back to a live DB query if the disk cache is missing or stale.
        /// Successful DB loads are written back to disk so the next SSMS restart is fast.
        /// </summary>
        private static async Task LoadSchemaAsync(string key, string connectionString, bool allowDiskCache)
        {
            // 1. Fast path: disk cache. Only use this for cold cache misses.
            // Stale in-memory entries must refresh from the server, otherwise a
            // stale disk snapshot can reload itself forever and repeatedly retrigger
            // the completion popup.
            if (allowDiskCache)
            {
                var fromDisk = await SchemaPersister.TryLoadAsync(key).ConfigureAwait(false);
                if (fromDisk != null && fromDisk.IsLoaded)
                {
                    _cache[key] = fromDisk;
                    _loadFailures.TryRemove(key, out _);
                    NotifySchemaLoaded(key, fromDisk);
                    return;
                }
            }

            // 2. Slow path: query the server.
            Logger.Diagnostic($"Loading schema for [{key}] from server...");
            var schema = await AsyncSchemaLoader.LoadAsync(connectionString).ConfigureAwait(false);

            if (schema.IsLoaded)
            {
                _cache[key] = schema;
                _loadFailures.TryRemove(key, out _);
                Logger.Log("Schema cached: " +
                           $"{schema.Tables.Count} tables, " +
                           $"{schema.Views.Count} views");

                // Best-effort disk write; don't block notification on it.
                _ = SchemaPersister.TrySaveAsync(key, schema);

                NotifySchemaLoaded(key, schema);
            }
            else
            {
                Logger.Warn("Schema load returned empty/failed");
                RecordLoadFailure(key, schema.LoadError);
            }
        }

        private static bool TryGetRecentFailure(string key, out SchemaLoadFailure failure)
        {
            if (_loadFailures.TryGetValue(key, out failure))
            {
                if ((DateTime.UtcNow - failure.FailedAt) < FailureRetryDelay)
                    return true;

                _loadFailures.TryRemove(key, out _);
            }

            failure = null;
            return false;
        }

        private static void RecordLoadFailure(string key, string message)
        {
            var safeMessage = string.IsNullOrWhiteSpace(message)
                ? "Could not connect to the active database"
                : message;

            _loadFailures[key] = new SchemaLoadFailure
            {
                Message = safeMessage,
                FailedAt = DateTime.UtcNow
            };

            NotifySchemaLoadFailed(key, safeMessage);
        }

        private static void NotifySchemaLoaded(string key, DatabaseSchema schema)
        {
            try
            {
                OnSchemaLoaded?.Invoke(key, schema);
            }
            catch (Exception ex)
            {
                Logger.Error("Error in OnSchemaLoaded handler", ex);
            }
        }

        private static void NotifySchemaLoadFailed(string key, string message)
        {
            try
            {
                OnSchemaLoadFailed?.Invoke(key, message);
            }
            catch (Exception ex)
            {
                Logger.Error("Error in OnSchemaLoadFailed handler", ex);
            }
        }

        /// <summary>
        /// Builds a cache key from server, database, and the effective connection
        /// string. The connection string is hashed so credentials never appear in logs
        /// or on disk, while different active SSMS query-window connections do not
        /// share a schema cache accidentally.
        /// </summary>
        private static string BuildKey(string server, string database, string connectionString)
        {
            return $"{server}|{database}|{BuildConnectionFingerprint(connectionString)}";
        }

        private static string BuildConnectionFingerprint(string connectionString)
        {
            return ConnectionStringFingerprint.Build(connectionString);
        }
    }
}
