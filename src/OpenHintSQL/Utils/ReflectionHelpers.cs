using System;
using System.IO;
using System.Reflection;

namespace OpenHintSQL.Utils
{
    /// <summary>
    /// Reflection helpers for accessing SSMS internal types and members.
    /// These exist so the project can compile without SSMS assemblies on the
    /// build machine while still calling into SSMS APIs at runtime.
    /// </summary>
    internal static class ReflectionHelpers
    {
        /// <summary>
        /// Finds an already-loaded type by full name across all loaded assemblies.
        /// </summary>
        public static Type FindLoadedType(string fullTypeName)
        {
            if (string.IsNullOrEmpty(fullTypeName))
                return null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullTypeName, throwOnError: false);
                    if (type != null)
                        return type;
                }
                catch
                {
                    // Skip assemblies that throw on metadata access (dynamic assemblies, etc.).
                }
            }

            return null;
        }

        /// <summary>
        /// Finds an already-loaded assembly by simple/partial name.
        /// </summary>
        public static Assembly FindLoadedAssembly(string partialName)
        {
            if (string.IsNullOrEmpty(partialName))
                return null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.FullName.StartsWith(partialName + ",", StringComparison.OrdinalIgnoreCase) ||
                        assembly.GetName().Name.Equals(partialName, StringComparison.OrdinalIgnoreCase))
                    {
                        return assembly;
                    }
                }
                catch
                {
                    // Skip assemblies that throw on metadata access.
                }
            }

            return null;
        }

        /// <summary>
        /// Best-effort load of an SSMS-shipped assembly by simple name. Tries
        /// already-loaded → Assembly.Load → LoadFrom (extension directory, then SSMS process directory).
        /// </summary>
        public static Assembly TryLoadSsmsAssembly(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName))
                return null;

            try
            {
                var loaded = FindLoadedAssembly(simpleName);
                if (loaded != null)
                    return loaded;

                return Assembly.Load(simpleName);
            }
            catch
            {
                // Fall through to LoadFrom below.
            }

            try
            {
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                var path = Path.Combine(baseDirectory, simpleName + ".dll");
                if (!File.Exists(path))
                {
                    var processDirectory = Path.GetDirectoryName(
                        System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName);
                    if (!string.IsNullOrEmpty(processDirectory))
                        path = Path.Combine(processDirectory, simpleName + ".dll");
                }

                if (File.Exists(path))
                {
                    var assembly = Assembly.LoadFrom(path);
                    Logger.Diagnostic($"Loaded SSMS assembly {simpleName} from {path}");
                    return assembly;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not load SSMS assembly {simpleName}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Invokes a method by name, preferring the interface's declaration when supplied
        /// (the concrete SSMS type may not have the method as public).
        /// </summary>
        public static object Invoke(object instance, Type interfaceType, string methodName, params object[] args)
        {
            if (instance == null)
                return null;

            try
            {
                var method = interfaceType?.GetMethod(methodName);
                if (method != null && interfaceType.IsInstanceOfType(instance))
                    return method.Invoke(instance, args);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Interface invoke failed for {methodName}: {ex.Message}");
            }

            return Invoke(instance, methodName, args);
        }

        /// <summary>
        /// Invokes a public instance method by name directly on the instance's runtime type.
        /// </summary>
        public static object Invoke(object instance, string methodName, params object[] args)
        {
            return instance?.GetType().GetMethod(methodName)?.Invoke(instance, args);
        }

        /// <summary>
        /// Reads a public instance property by name.
        /// </summary>
        public static object GetPropertyValue(object instance, string propertyName)
        {
            return instance?.GetType().GetProperty(propertyName)?.GetValue(instance);
        }

        /// <summary>
        /// Reads a non-public instance field by name, walking the inheritance chain.
        /// Returns null if the field is missing on the instance's type.
        /// </summary>
        public static object GetNonPublicField(object instance, string fieldName)
        {
            if (instance == null || string.IsNullOrEmpty(fieldName))
                return null;

            var type = instance.GetType();
            while (type != null)
            {
                var field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                    return field.GetValue(instance);
                type = type.BaseType;
            }

            return null;
        }

        /// <summary>
        /// Reads a property (public or non-public) by name, walking the inheritance chain.
        /// Useful for SSMS types that expose key surfaces on a non-public base class.
        /// </summary>
        public static object GetPropertyValueDeep(object instance, string propertyName)
        {
            if (instance == null || string.IsNullOrEmpty(propertyName))
                return null;

            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = instance.GetType();
            while (type != null)
            {
                var prop = type.GetProperty(propertyName, flags);
                if (prop != null)
                    return prop.GetValue(instance);
                type = type.BaseType;
            }

            return null;
        }
    }
}
