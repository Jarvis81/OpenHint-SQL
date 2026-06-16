using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.SqlServer.Management.UI.Grid;
using Microsoft.VisualStudio.Shell;
using OpenHintSQL.Utils;

namespace OpenHintSQL.ResultGridScripting
{
    internal static class ResultGridLocator
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        public static IGridControl GetFocusedResultGrid()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // After the context menu closes Windows returns focus to the control that
            // showed it — the result grid. Walk up from the focused HWND first.
            var grid = TryGetFocusedControl();
            if (grid != null)
                return grid;

            // Fallback: BFS over Application.OpenForms looking for an IGridControl
            // with a non-empty selection (retained after menu close).
            IGridControl firstGrid = null;
            foreach (Form form in Application.OpenForms)
            {
                var found = FindGridWithSelection(form, ref firstGrid);
                if (found != null)
                    return found;
            }

            if (firstGrid == null)
                Logger.Warn("No result grid found. Run a query first.");

            return firstGrid;
        }

        private static IGridControl TryGetFocusedControl()
        {
            try
            {
                var hwnd = GetFocus();
                if (hwnd == IntPtr.Zero) return null;
                var ctrl = Control.FromHandle(hwnd);
                while (ctrl != null)
                {
                    if (ctrl is IGridControl g) return g;
                    ctrl = ctrl.Parent;
                }
            }
            catch (Exception ex)
            {
                Logger.Diagnostic($"GetFocus walk failed: {ex.Message}");
            }
            return null;
        }

        private static IGridControl FindGridWithSelection(Control root, ref IGridControl firstGrid)
        {
            if (root == null) return null;
            var queue = new Queue<Control>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                Control current;
                try { current = queue.Dequeue(); }
                catch { continue; }
                if (current == null) continue;
                try { if (current.IsDisposed) continue; }
                catch { continue; }

                if (current is IGridControl grid)
                {
                    if (firstGrid == null)
                        firstGrid = grid;
                    try
                    {
                        var cells = grid.SelectedCells;
                        if (cells != null && cells.Count > 0)
                            return grid;
                    }
                    catch (Exception ex)
                    {
                        Logger.Diagnostic($"SelectedCells check failed: {ex.Message}");
                    }
                }

                Control.ControlCollection children;
                try { children = current.Controls; }
                catch { continue; }
                if (children == null) continue;
                foreach (Control child in children)
                    queue.Enqueue(child);
            }
            return null;
        }
    }
}
