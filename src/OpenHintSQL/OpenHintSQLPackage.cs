using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace OpenHintSQL
{
    /// <summary>
    /// VSPackage entry point for OpenHintSQL.
    /// Initializes the extension when SSMS loads.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideAutoLoad(UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    public sealed class OpenHintSQLPackage : AsyncPackage
    {
        public const string PackageGuidString = "63D8CFAD-D1FF-40EB-80DB-7728DEDD7A91";

        /// <summary>
        /// Singleton instance of the package.
        /// </summary>
        public static OpenHintSQLPackage Instance { get; private set; }

        // SSMS assemblies compiled at a fixed version; redirected at runtime to whatever
        // version SSMS has loaded so the extension works across 18/19/20/21/22+.
        private static readonly string[] SsmsRedirectableAssemblies =
        {
            "Microsoft.SqlServer.GridControl",
            "Microsoft.SqlServer.DlgGrid",
            "Microsoft.SqlServer.Management.UI.VSIntegration",
            "Microsoft.SqlServer.ConnectionInfo",
            "SqlWorkbench.Interfaces",
            "Microsoft.SqlServer.RegSvrEnum",
            "SqlPackageBase",
        };

        static OpenHintSQLPackage()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveSsmsAssembly;
        }

        // Must stay self-contained (no Logger) — runs before package initialization.
        private static Assembly ResolveSsmsAssembly(object sender, ResolveEventArgs args)
        {
            string simpleName;
            try { simpleName = new AssemblyName(args.Name).Name; }
            catch { return null; }

            bool handled = false;
            foreach (var name in SsmsRedirectableAssemblies)
            {
                if (string.Equals(name, simpleName, StringComparison.OrdinalIgnoreCase)) { handled = true; break; }
            }
            if (!handled) return null;

            // Prefer the exact instance SSMS already loaded — same one the live grid uses.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (string.Equals(asm.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                        return asm;
                }
                catch { }
            }

            try
            {
                var ideDir = System.IO.Path.GetDirectoryName(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName);
                if (!string.IsNullOrEmpty(ideDir))
                {
                    var path = System.IO.Path.Combine(ideDir, simpleName + ".dll");
                    if (System.IO.File.Exists(path))
                        return Assembly.LoadFrom(path);
                }
            }
            catch { }

            return null;
        }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // Switch to UI thread for any UI-related initialization
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            Instance = this;

            Utils.Logger.Initialize(this);
            Utils.Logger.Log("OpenHintSQL v1.1.0 loaded successfully.");
            Utils.Logger.Diagnostic($"SSMS Process: {System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName}");
            Utils.Logger.Log($"Process Architecture: {(IntPtr.Size == 8 ? "64-bit" : "32-bit")}");
            Utils.Logger.Log($"VS Shell SDK Version: {typeof(Package).Assembly.GetName().Version}");

            // Load snippets configuration
            try
            {
                var snippetProvider = Snippets.SnippetProvider.Instance;
                Utils.Logger.Log($"Loaded {snippetProvider.Count} snippets.");
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Warning: Failed to load snippets: {ex.Message}");
            }

            try
            {
                ResultGridScripting.InsertScriptCommand.Initialize(this);
            }
            catch (Exception ex)
            {
                Utils.Logger.Log($"Warning: INSERT script feature failed to initialize: {ex.Message}");
            }
        }
    }
}
