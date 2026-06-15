using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using OpenHintSQL.Commands;
using OpenHintSQL.Utils;

namespace OpenHintSQL.ResultGridScripting
{
    /// <summary>
    /// Entry point for the "Generate as INSERT script" command. Registered
    /// once with the OleMenuCommandService so both the VSCT-defined Tools
    /// menu entry and the result-grid context-menu item (injected at runtime)
    /// route through the same callback.
    /// </summary>
    internal static class InsertScriptCommand
    {
        private static AsyncPackage _package;

        public static void Initialize(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _package = package ?? throw new ArgumentNullException(nameof(package));

            var commandService = ((IServiceProvider)package).GetService(typeof(IMenuCommandService))
                as IMenuCommandService;
            if (commandService == null)
            {
                Logger.Warn("IMenuCommandService unavailable; INSERT script command not bound.");
                return;
            }

            var commandId = new CommandID(
                PackageCommandGuids.CmdSet,
                PackageCommandGuids.GenerateInsertScriptCmdId);
            var menuItem = new OleMenuCommand(OnExecute, commandId);
            commandService.AddCommand(menuItem);
            Logger.Log("Generate INSERT script command registered.");
        }

        /// <summary>
        /// Public entry point shared with the result-grid context-menu hook —
        /// the WinForms ToolStripMenuItem click handler calls this directly so
        /// failures are surfaced through the same logging / message-box path.
        /// </summary>
        public static void Execute()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var grid = ResultGridLocator.GetFocusedResultGrid();
                if (grid == null)
                {
                    ShowMessage("OpenHint SQL: No active result grid was found. Run a query and try again.", isError: true);
                    return;
                }

                var selection = ResultGridSelectionReader.ReadSelection(grid);
                if (!selection.Success)
                {
                    ShowMessage("OpenHint SQL: " + selection.ErrorMessage, isError: true);
                    return;
                }

                var headers = ResultGridColumnReader.ReadHeaders(grid, selection.Range.StartColumn, selection.Range.ColumnCount);
                var values  = ResultGridSelectionReader.ReadValues(grid, selection.Range);
                var script  = InsertScriptBuilder.Build(headers, values);

                if (!NewQueryWindowOpener.OpenWithScript(script))
                {
                    ShowMessage("OpenHint SQL: Failed to open a new query window. See Output → OpenHint SQL for details.", isError: true);
                    return;
                }

                Logger.Log($"Generated INSERT script: {selection.Range.RowCount} row(s), {selection.Range.ColumnCount} col(s).");
            }
            catch (Exception ex)
            {
                Logger.Error("Generate INSERT script failed", ex);
                ShowMessage("OpenHint SQL: Generate INSERT script failed. See Output → OpenHint SQL for details.", isError: true);
            }
        }

        private static void OnExecute(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Execute();
        }

        private static void ShowMessage(string message, bool isError)
        {
            try
            {
                VsShellUtilities.ShowMessageBox(
                    _package,
                    message,
                    "OpenHint SQL",
                    isError ? OLEMSGICON.OLEMSGICON_WARNING : OLEMSGICON.OLEMSGICON_INFO,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
            catch
            {
                // Suppress — message box failures must not break the workflow.
            }
        }
    }
}
