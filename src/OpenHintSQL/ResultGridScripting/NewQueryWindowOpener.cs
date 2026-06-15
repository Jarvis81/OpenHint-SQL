using System;
using System.Reflection;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using OpenHintSQL.Utils;

namespace OpenHintSQL.ResultGridScripting
{
    /// <summary>
    /// Opens a new SSMS query tab and writes the supplied script into it.
    ///
    /// Step 1 — ServiceCache.ScriptFactory.CreateNewBlankScript(ScriptType.Sql)
    ///   uses reflection because the ScriptType enum lives in an SSMS-internal
    ///   assembly the project does not directly reference.
    ///
    /// Step 2 — the new document becomes DTE.ActiveDocument, whose TextDocument
    ///   object exposes a StartPoint we can use to inject text. Accessed via
    ///   COM late binding (dynamic) so no EnvDTE assembly reference is needed.
    /// </summary>
    internal static class NewQueryWindowOpener
    {
        public static bool OpenWithScript(string scriptText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrEmpty(scriptText))
                return false;

            if (!CreateBlankSqlScript())
                return false;

            return InjectIntoActiveDocument(scriptText);
        }

        private static bool CreateBlankSqlScript()
        {
            var serviceCacheType = ReflectionHelpers.FindLoadedType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache");
            if (serviceCacheType == null)
            {
                Logger.Warn("Cannot open new query window: ServiceCache type not loaded.");
                return false;
            }

            var scriptFactory = serviceCacheType
                .GetProperty("ScriptFactory", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null);
            if (scriptFactory == null)
            {
                Logger.Warn("Cannot open new query window: ScriptFactory unavailable.");
                return false;
            }

            // ScriptType enum lives in Microsoft.SqlServer.Management.UI.VSIntegration.
            var scriptTypeEnum = ReflectionHelpers.FindLoadedType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.Editors.ScriptType");
            object sqlScriptValue = null;
            if (scriptTypeEnum != null)
            {
                try { sqlScriptValue = Enum.Parse(scriptTypeEnum, "Sql"); }
                catch (Exception ex) { Logger.Warn($"Could not parse ScriptType.Sql: {ex.Message}"); }
            }

            try
            {
                // GetMethod(name, flags) throws AmbiguousMatchException when SSMS
                // exposes multiple CreateNewBlankScript overloads (seen on SSMS 22).
                // Pick the best overload explicitly:
                //  1. If we have the ScriptType enum value, prefer the overload whose
                //     first parameter type matches (most specific, avoids wrong overload).
                //  2. Otherwise fall back to any single-parameter public instance overload.
                MethodInfo createMethod = null;
                var candidates = scriptFactory.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                if (scriptTypeEnum != null && sqlScriptValue != null)
                {
                    foreach (var m in candidates)
                    {
                        if (m.Name != "CreateNewBlankScript") continue;
                        var p = m.GetParameters();
                        if (p.Length >= 1 && p[0].ParameterType == scriptTypeEnum)
                        {
                            // Prefer the overload whose ONLY required parameter is ScriptType.
                            // If multiple match, prefer the one with fewest parameters.
                            if (createMethod == null || m.GetParameters().Length < createMethod.GetParameters().Length)
                                createMethod = m;
                        }
                    }
                }
                if (createMethod == null)
                {
                    // Fallback: any overload named CreateNewBlankScript with ≥1 param.
                    foreach (var m in candidates)
                    {
                        if (m.Name != "CreateNewBlankScript") continue;
                        if (createMethod == null || m.GetParameters().Length < createMethod.GetParameters().Length)
                            createMethod = m;
                    }
                }
                if (createMethod == null)
                {
                    Logger.Warn("ScriptFactory.CreateNewBlankScript method not found.");
                    return false;
                }

                // Build an args array that matches the chosen overload: first arg is
                // the ScriptType value (or null); remaining optional params get defaults.
                var methodParams = createMethod.GetParameters();
                var args = new object[methodParams.Length];
                if (args.Length > 0) args[0] = sqlScriptValue;
                for (int i = 1; i < methodParams.Length; i++)
                    args[i] = methodParams[i].HasDefaultValue ? methodParams[i].DefaultValue : Type.Missing;

                createMethod.Invoke(scriptFactory, args);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("CreateNewBlankScript failed", ex);
                return false;
            }
        }

        private static bool InjectIntoActiveDocument(string scriptText)
        {
            try
            {
                var dte = Package.GetGlobalService(typeof(SDTE));
                if (dte == null)
                {
                    Logger.Warn("DTE service unavailable; cannot write into the new query window.");
                    return false;
                }

                dynamic dteDynamic = dte;
                dynamic activeDocument = dteDynamic.ActiveDocument;
                if (activeDocument == null)
                {
                    Logger.Warn("DTE.ActiveDocument was null after CreateNewBlankScript — the new tab did not focus.");
                    return false;
                }

                dynamic textDocument = activeDocument.Object("TextDocument");
                if (textDocument == null)
                {
                    Logger.Warn("ActiveDocument.Object(\"TextDocument\") returned null — not a text editor.");
                    return false;
                }

                dynamic editPoint = textDocument.StartPoint.CreateEditPoint();
                editPoint.Insert(scriptText);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("Could not inject script into new query window", ex);
                return false;
            }
        }
    }
}
