using System;
using Microsoft.SqlServer.Management.UI.Grid;

namespace OpenHintSQL.ResultGridScripting
{
    /// <summary>
    /// Describes a validated single-rectangle selection inside an SSMS result grid.
    /// Row indices are 64-bit because SSMS supports very large result sets.
    /// </summary>
    internal sealed class SelectionRange
    {
        public long StartRow { get; }
        public int  StartColumn { get; }   // UI column index (storage index resolved on read)
        public long RowCount { get; }
        public int  ColumnCount { get; }

        public SelectionRange(long startRow, int startColumn, long rowCount, int columnCount)
        {
            StartRow = startRow;
            StartColumn = startColumn;
            RowCount = rowCount;
            ColumnCount = columnCount;
        }
    }

    internal sealed class SelectionResult
    {
        public bool Success { get; }
        public string ErrorMessage { get; }
        public SelectionRange Range { get; }

        private SelectionResult(bool success, string errorMessage, SelectionRange range)
        {
            Success = success;
            ErrorMessage = errorMessage;
            Range = range;
        }

        public static SelectionResult Ok(SelectionRange range) => new SelectionResult(true, null, range);
        public static SelectionResult Fail(string message) => new SelectionResult(false, message, null);
    }

    internal static class ResultGridSelectionReader
    {
        /// <summary>
        /// Validates the grid's selection and returns a single rectangular range.
        /// Selections covering multiple disjoint blocks (e.g. ctrl+click two
        /// regions) or empty selections are rejected — callers are expected to
        /// show the ErrorMessage to the user.
        /// </summary>
        public static SelectionResult ReadSelection(IGridControl grid)
        {
            if (grid == null)
                return SelectionResult.Fail("No active result grid was found.");

            BlockOfCellsCollection blocks = grid.SelectedCells;
            if (blocks == null || blocks.Count == 0)
                return SelectionResult.Fail("Select one or more cells in the result grid first.");

            if (blocks.Count > 1)
                return SelectionResult.Fail(
                    "Selection must be a single contiguous rectangle of cells. " +
                    "Multi-block selections (e.g. Ctrl+click of separate regions) are not supported.");

            var block = blocks[0];
            if (block.IsEmpty || block.Width <= 0 || block.Height <= 0)
                return SelectionResult.Fail("Selection is empty.");

            return SelectionResult.Ok(new SelectionRange(
                startRow: block.Y,
                startColumn: block.X,
                rowCount: block.Height,
                columnCount: block.Width));
        }

        /// <summary>
        /// Materialises selected cell values into a [row, col] string matrix.
        /// Each cell is read via IGridStorage.GetCellDataAsString — the same
        /// path SSMS uses for "Copy with Headers".
        ///
        /// SSMS result grids reserve UI column index 0 for the row-number
        /// header; we resolve UI → storage index per column so iteration works
        /// regardless of whether the user dragged from the row header or not.
        /// </summary>
        public static string[,] ReadValues(IGridControl grid, SelectionRange range)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            if (range == null) throw new ArgumentNullException(nameof(range));

            var storage = grid.GridStorage
                ?? throw new InvalidOperationException("Result grid has no GridStorage; the query may not have completed.");

            int colCount = range.ColumnCount;
            long rowCount = range.RowCount;
            var values = new string[rowCount, colCount];

            for (int c = 0; c < colCount; c++)
            {
                int uiCol = range.StartColumn + c;
                int storageCol;
                try
                {
                    storageCol = grid.GetStorageColumnIndexByUIIndex(uiCol);
                }
                catch
                {
                    storageCol = uiCol;
                }

                for (long r = 0; r < rowCount; r++)
                {
                    long row = range.StartRow + r;
                    string value;
                    try
                    {
                        value = storage.GetCellDataAsString(row, storageCol);
                    }
                    catch
                    {
                        value = string.Empty;
                    }
                    values[r, c] = value ?? string.Empty;
                }
            }

            return values;
        }
    }
}
