using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Schema
{
    internal static class ProcedureCache
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan FailureRetryDelay = TimeSpan.FromSeconds(15);

        private static readonly ConcurrentDictionary<string, ProcedureList> _cache
            = new ConcurrentDictionary<string, ProcedureList>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, Task> _loadingTasks
            = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, ProcedureLoadFailure> _loadFailures
            = new ConcurrentDictionary<string, ProcedureLoadFailure>(StringComparer.OrdinalIgnoreCase);

        public static event Action<string, ProcedureList> OnProceduresLoaded;

        public static event Action<string, string> OnProcedureLoadFailed;

        private sealed class ProcedureLoadFailure
        {
            public string Message { get; set; }
            public DateTime FailedAt { get; set; }
        }

        public static ProcedureList GetOrLoad(string server, string database, string connectionString)
        {
            var key = BuildKey(server, database, connectionString);

            if (_cache.TryGetValue(key, out var cached) && cached.IsLoaded)
            {
                if ((DateTime.UtcNow - cached.LoadedAt) < CacheTtl)
                    return cached;

                Logger.Diagnostic($"Procedure list stale for [{key}], triggering background refresh");
                StartBackgroundLoad(key, connectionString);
                return cached;
            }

            if (TryGetRecentFailure(key, out _))
                return ProcedureList.Empty;

            Logger.Diagnostic($"Procedure cache miss for [{key}], starting background load");
            StartBackgroundLoad(key, connectionString);
            return ProcedureList.Empty;
        }

        public static string GetLastLoadError(string server, string database, string connectionString)
        {
            var key = BuildKey(server, database, connectionString);
            return TryGetRecentFailure(key, out var failure) ? failure.Message : null;
        }

        public static void Clear()
        {
            _cache.Clear();
            _loadingTasks.Clear();
            _loadFailures.Clear();
            Logger.Log("Procedure cache cleared");
        }

        private static void StartBackgroundLoad(string key, string connectionString)
        {
            _loadingTasks.GetOrAdd(key, k =>
            {
                _loadFailures.TryRemove(k, out _);

                return Task.Run(async () =>
                {
                    try
                    {
                        await LoadProcedureListAsync(k, connectionString).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Background procedure list load failed", ex);
                        RecordLoadFailure(k, ex.Message);
                    }
                    finally
                    {
                        _loadingTasks.TryRemove(k, out _);
                    }
                });
            });
        }

        private static async Task LoadProcedureListAsync(string key, string connectionString)
        {
            Logger.Diagnostic($"Loading procedure list for [{key}] from server...");
            var procedures = await AsyncProcedureLoader.LoadAsync(connectionString).ConfigureAwait(false);

            if (procedures.IsLoaded)
            {
                _cache[key] = procedures;
                _loadFailures.TryRemove(key, out _);
                NotifyProceduresLoaded(key, procedures);
            }
            else
            {
                Logger.Warn("Procedure list load returned empty/failed");
                RecordLoadFailure(key, procedures.LoadError);
            }
        }

        private static bool TryGetRecentFailure(string key, out ProcedureLoadFailure failure)
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
                ? "Could not load stored procedures from the active database"
                : message;

            _loadFailures[key] = new ProcedureLoadFailure
            {
                Message = safeMessage,
                FailedAt = DateTime.UtcNow
            };

            NotifyProcedureLoadFailed(key, safeMessage);
        }

        private static void NotifyProceduresLoaded(string key, ProcedureList procedures)
        {
            try
            {
                OnProceduresLoaded?.Invoke(key, procedures);
            }
            catch (Exception ex)
            {
                Logger.Error("Error in OnProceduresLoaded handler", ex);
            }
        }

        private static void NotifyProcedureLoadFailed(string key, string message)
        {
            try
            {
                OnProcedureLoadFailed?.Invoke(key, message);
            }
            catch (Exception ex)
            {
                Logger.Error("Error in OnProcedureLoadFailed handler", ex);
            }
        }

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
