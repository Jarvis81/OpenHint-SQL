using System;
using System.Data;
using System.Data.SqlClient;
using System.Reflection;
using Microsoft.VisualStudio.Shell;
using OpenHintSQL.Utils;

namespace OpenHintSQL.Connection
{
    /// <summary>
    /// Simple data class holding the active SSMS connection details.
    /// </summary>
    public class ConnectionInfo
    {
        /// <summary>SQL Server instance name (e.g. localhost, MYSERVER\SQL2019).</summary>
        public string Server { get; set; }

        /// <summary>Database name (e.g. master, AdventureWorks).</summary>
        public string Database { get; set; }

        /// <summary>ADO.NET connection string built from the SSMS connection.</summary>
        public string ConnectionString { get; set; }
    }

    /// <summary>
    /// Extracts the active query-window connection from SSMS internal APIs.
    /// Uses reflection so the project can compile without SSMS assemblies installed
    /// on the build machine.
    /// </summary>
    internal static class ConnectionTracker
    {
        /// <summary>
        /// Gets the connection info for the currently active SSMS query window.
        /// Returns null if no connection is available or if SSMS APIs cannot be accessed.
        /// This method is safe to call from any thread but should ideally be called on the UI thread.
        /// </summary>
        /// <returns>A <see cref="ConnectionInfo"/> or null.</returns>
        public static ConnectionInfo GetActiveConnection()
        {
            try
            {
                var serviceConnection = GetConnectionViaSqlEditorService();
                if (serviceConnection != null)
                    return serviceConnection;
            }
            catch (Exception ex)
            {
                Logger.Warn($"SqlEditorService connection retrieval failed: {ex.Message}");
            }

            try
            {
                return GetConnectionViaReflection();
            }
            catch (Exception ex)
            {
                Logger.Warn($"Reflection-based connection retrieval failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Preferred path for SSMS 21/22 and newer SSMS builds. The editor service
        /// knows the active query editor and exposes its live ADO.NET connection.
        /// </summary>
        private static ConnectionInfo GetConnectionViaSqlEditorService()
        {
            if (!ThreadHelper.CheckAccess())
            {
                return ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    return GetConnectionViaSqlEditorServiceOnUiThread();
                });
            }

            return GetConnectionViaSqlEditorServiceOnUiThread();
        }

        private static ConnectionInfo GetConnectionViaSqlEditorServiceOnUiThread()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var serviceType = FindLoadedType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.SSqlEditorService");
            if (serviceType == null)
            {
                Logger.Log("SSqlEditorService not available; using legacy connection lookup");
                return null;
            }

            var interfaceType = FindLoadedType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ISqlEditorService");

            var editorService = ResolveVisualStudioService(serviceType, interfaceType);
            if (editorService == null)
            {
                Logger.Warn("SSqlEditorService returned null");
                return null;
            }

            Logger.Diagnostic($"SSqlEditorService resolved as {editorService.GetType().FullName}");

            var activeDetails = Invoke(editorService, interfaceType, "GetActiveEditorDetails");
            string moniker = GetPropertyValue(activeDetails, "Moniker") as string;
            if (string.IsNullOrEmpty(moniker))
                moniker = Invoke(editorService, interfaceType, "GetActiveEditorMoniker") as string;

            Logger.Diagnostic($"SSqlEditorService active moniker: {(string.IsNullOrEmpty(moniker) ? "<empty>" : moniker)}");

            object uiConnInfo = null;
            if (!string.IsNullOrEmpty(moniker))
                uiConnInfo = Invoke(editorService, interfaceType, "GetUIConnectionInfoForSpecificQueryEditor", moniker);

            var dbConnection = Invoke(editorService, interfaceType, "GetCurrentConnection") as IDbConnection;
            if (dbConnection == null && !string.IsNullOrEmpty(moniker))
                dbConnection = Invoke(editorService, interfaceType, "GetConnectionForSpecificQueryEditor", moniker) as IDbConnection;

            var info = BuildConnectionInfo(dbConnection, uiConnInfo);
            if (info != null)
            {
                Logger.Log("Connection obtained (SqlEditorService)");
                return info;
            }

            Logger.Warn("SqlEditorService did not expose an active connection");
            return null;
        }

        /// <summary>
        /// Uses reflection to access SSMS connection APIs that are loaded inside SSMS
        /// but may not be present on developer or CI machines.
        /// </summary>
        private static ConnectionInfo GetConnectionViaReflection()
        {
            var serviceCacheType = FindLoadedType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache");
            if (serviceCacheType == null)
            {
                TryLoadSsmsAssembly("Microsoft.SqlServer.SqlTools.VSIntegration");
                TryLoadSsmsAssembly("Microsoft.SqlServer.Management.UI.VSIntegration");
                serviceCacheType = FindLoadedType(
                    "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache");
            }

            if (serviceCacheType == null)
            {
                Logger.Warn("ServiceCache type not found");
                return null;
            }

            var scriptFactoryProp = serviceCacheType.GetProperty(
                "ScriptFactory",
                BindingFlags.Public | BindingFlags.Static);
            var scriptFactory = scriptFactoryProp?.GetValue(null);
            if (scriptFactory == null)
            {
                Logger.Warn("ScriptFactory returned null via ServiceCache; trying global IScriptFactory service");

                var scriptFactoryType = FindLoadedType(
                    "Microsoft.SqlServer.Management.UI.VSIntegration.Editors.IScriptFactory");
                if (scriptFactoryType == null)
                {
                    TryLoadSsmsAssembly("SqlWorkbench.Interfaces");
                    scriptFactoryType = FindLoadedType(
                        "Microsoft.SqlServer.Management.UI.VSIntegration.Editors.IScriptFactory");
                }

                if (scriptFactoryType != null)
                    scriptFactory = ResolveVisualStudioService(scriptFactoryType, scriptFactoryType);

                if (scriptFactory == null)
                {
                    Logger.Warn("ScriptFactory unavailable via reflection");
                    return null;
                }
            }

            var connInfoWrapper = GetPropertyValue(scriptFactory, "CurrentlyActiveWndConnectionInfo");
            if (connInfoWrapper == null)
            {
                Logger.Warn("CurrentlyActiveWndConnectionInfo returned null");
                return null;
            }

            var uiConnInfo = GetPropertyValue(connInfoWrapper, "UIConnectionInfo");
            if (uiConnInfo == null)
            {
                Logger.Warn("UIConnectionInfo is null (reflection)");
                return null;
            }

            var info = BuildConnectionInfo(null, uiConnInfo);
            if (info != null)
            {
                Logger.Log("Connection obtained (reflection)");
                return info;
            }

            Logger.Warn("UIConnectionInfo could not be converted to a connection");
            return null;
        }

        private static ConnectionInfo BuildConnectionInfo(IDbConnection dbConnection, object uiConnInfo)
        {
            string server = null;
            string database = null;
            string connectionString = null;

            if (dbConnection != null)
            {
                server = GetFirstStringProperty(dbConnection, "DataSource");
                database = GetFirstStringProperty(dbConnection, "Database");
                connectionString = dbConnection.ConnectionString;
            }

            if (uiConnInfo != null)
            {
                server = server ?? GetFirstStringProperty(uiConnInfo, "ServerName", "Server", "DataSource");
                database = database ??
                    GetFirstStringProperty(uiConnInfo, "DatabaseName", "Database", "InitialCatalog") ??
                    GetAdvancedOption(uiConnInfo, "DATABASE", "Database", "Initial Catalog", "InitialCatalog");
            }

            if (string.IsNullOrEmpty(server))
            {
                Logger.Warn("Server name is empty");
                return null;
            }

            if (string.IsNullOrEmpty(database))
                database = "master";

            var userName = GetPropertyValue(uiConnInfo, "UserName") as string;
            var password = GetPropertyValue(uiConnInfo, "Password") as string;
            var authenticationType = Convert.ToString(GetPropertyValue(uiConnInfo, "AuthenticationType"));
            bool hasAuthenticationHints = uiConnInfo != null &&
                (!string.IsNullOrWhiteSpace(userName) ||
                 !string.IsNullOrWhiteSpace(password) ||
                 !string.IsNullOrWhiteSpace(authenticationType));
            bool shouldUseIntegratedSecurity = ShouldUseIntegratedSecurity(userName, password, authenticationType);
            int connectTimeout = shouldUseIntegratedSecurity ? 60 : 10;
            Logger.Diagnostic($"Connection auth hint: type='{(string.IsNullOrWhiteSpace(authenticationType) ? "<empty>" : authenticationType)}', hasUser={!string.IsNullOrWhiteSpace(userName)}, hasPassword={!string.IsNullOrEmpty(password)}");

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                try
                {
                    var existing = new SqlConnectionStringBuilder(NormalizeConnectionString(connectionString))
                    {
                        DataSource = server,
                        InitialCatalog = database,
                        ApplicationName = "OpenHintSQL",
                        ConnectTimeout = connectTimeout
                    };

                    if (hasAuthenticationHints)
                        ApplyAuthentication(existing, shouldUseIntegratedSecurity, userName, password);
                    Logger.Diagnostic($"Connection auth mode: {(existing.IntegratedSecurity ? "Windows Integrated" : "SQL/User")}");

                    return new ConnectionInfo
                    {
                        Server = server,
                        Database = database,
                        ConnectionString = existing.ConnectionString
                    };
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Could not reuse active connection string: {ex.Message}");
                }
            }

            var builder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                InitialCatalog = database,
                ApplicationName = "OpenHintSQL",
                ConnectTimeout = connectTimeout
            };

            ApplyAuthentication(builder, shouldUseIntegratedSecurity, userName, password);
            Logger.Diagnostic($"Connection auth mode: {(builder.IntegratedSecurity ? "Windows Integrated" : "SQL/User")}");

            return new ConnectionInfo
            {
                Server = server,
                Database = database,
                ConnectionString = builder.ConnectionString
            };
        }

        private static string NormalizeConnectionString(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return connectionString;

            var normalized = connectionString
                .Replace("Multiple Active Result Sets", "MultipleActiveResultSets")
                .Replace("Trust Server Certificate", "TrustServerCertificate");

            return RemoveConnectionStringKeywords(normalized, "Command Timeout");
        }

        private static string RemoveConnectionStringKeywords(string connectionString, params string[] keysToRemove)
        {
            if (string.IsNullOrWhiteSpace(connectionString) || keysToRemove == null || keysToRemove.Length == 0)
                return connectionString;

            var parts = connectionString.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new System.Collections.Generic.List<string>();
            foreach (var part in parts)
            {
                var separatorIndex = part.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    kept.Add(part);
                    continue;
                }

                var key = part.Substring(0, separatorIndex).Trim();
                bool remove = false;
                foreach (var keyToRemove in keysToRemove)
                {
                    if (string.Equals(key, keyToRemove, StringComparison.OrdinalIgnoreCase))
                    {
                        remove = true;
                        break;
                    }
                }

                if (!remove)
                    kept.Add(part);
            }

            return string.Join(";", kept);
        }

        private static bool ShouldUseIntegratedSecurity(string userName, string password, string authenticationType)
        {
            if (!string.IsNullOrWhiteSpace(authenticationType))
            {
                if (authenticationType.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    authenticationType.IndexOf("integrated", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                if (authenticationType.IndexOf("sql", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }

            // SSMS often keeps the Windows account in UserName and leaves Password empty.
            // Treat that as Windows Auth; otherwise System.Data.SqlClient attempts SQL Auth
            // with a domain-style login and schema loading fails.
            if (string.IsNullOrEmpty(password))
                return true;

            return string.IsNullOrEmpty(userName);
        }

        private static void ApplyAuthentication(
            SqlConnectionStringBuilder builder,
            bool useIntegratedSecurity,
            string userName,
            string password)
        {
            if (builder == null)
                return;

            if (useIntegratedSecurity)
            {
                builder.IntegratedSecurity = true;
                RemoveCredentialKeywords(builder);
                return;
            }

            builder.IntegratedSecurity = false;
            if (!string.IsNullOrWhiteSpace(userName))
                builder.UserID = userName;
            if (!string.IsNullOrEmpty(password))
                builder.Password = password;
        }

        private static void RemoveCredentialKeywords(SqlConnectionStringBuilder builder)
        {
            builder.Remove("User ID");
            builder.Remove("Password");
            builder.Remove("UID");
            builder.Remove("PWD");
        }

        private static object ResolveVisualStudioService(Type serviceType, Type interfaceType)
        {
            var package = OpenHintSQLPackage.Instance;

            object service = null;
            if (package != null)
            {
                service = TryGetService("package marker", () => ((IServiceProvider)package).GetService(serviceType));
                if (service == null && interfaceType != null)
                    service = TryGetService("package interface", () => ((IServiceProvider)package).GetService(interfaceType));
            }
            else
            {
                Logger.Warn("OpenHintSQLPackage.Instance is null");
            }

            if (service == null)
                service = TryGetService("global marker", () => Package.GetGlobalService(serviceType));

            if (service == null && interfaceType != null)
                service = TryGetService("global interface", () => Package.GetGlobalService(interfaceType));

            if (service == null)
                service = TryGetService("global provider marker", () => Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(serviceType));

            if (service == null && interfaceType != null)
                service = TryGetService("global provider interface", () => Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider.GetService(interfaceType));

            return service;
        }

        private static object TryGetService(string source, Func<object> getService)
        {
            try
            {
                var service = getService();
                Logger.Diagnostic($"{source}: {(service == null ? "null" : service.GetType().FullName)}");
                return service;
            }
            catch (Exception ex)
            {
                Logger.Warn($"{source} failed: {ex.Message}");
                return null;
            }
        }

        private static object Invoke(object instance, Type interfaceType, string methodName, params object[] args)
            => ReflectionHelpers.Invoke(instance, interfaceType, methodName, args);

        private static object Invoke(object instance, string methodName, params object[] args)
            => ReflectionHelpers.Invoke(instance, methodName, args);

        private static object GetPropertyValue(object instance, string propertyName)
            => ReflectionHelpers.GetPropertyValue(instance, propertyName);

        private static string GetFirstStringProperty(object instance, params string[] propertyNames)
        {
            if (instance == null || propertyNames == null)
                return null;

            foreach (var propertyName in propertyNames)
            {
                var value = GetPropertyValue(instance, propertyName) as string;
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        private static string GetAdvancedOption(object uiConnInfo, params string[] keys)
        {
            var advOptions = GetPropertyValue(uiConnInfo, "AdvancedOptions");
            if (advOptions == null)
                return null;

            var indexer = advOptions.GetType().GetProperty("Item", new[] { typeof(string) });
            if (indexer == null)
                return null;

            foreach (var key in keys)
            {
                try
                {
                    var value = indexer.GetValue(advOptions, new object[] { key }) as string;
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
                catch
                {
                    // Some SSMS versions throw for unknown advanced option keys.
                }
            }

            return null;
        }

        private static Type FindLoadedType(string fullTypeName)
            => ReflectionHelpers.FindLoadedType(fullTypeName);

        private static Assembly TryLoadSsmsAssembly(string simpleName)
            => ReflectionHelpers.TryLoadSsmsAssembly(simpleName);
    }
}
