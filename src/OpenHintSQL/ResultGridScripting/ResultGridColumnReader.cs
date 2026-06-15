using System.Drawing;
using Microsoft.SqlServer.Management.UI.Grid;

namespace OpenHintSQL.ResultGridScripting
{
    /// <summary>
    /// Reads header text for a contiguous span of UI columns.
    /// IGridControl.GetHeaderInfo writes the header into an out parameter and
    /// returns nothing; we wrap that to produce a plain string array.
    /// </summary>
    internal static class ResultGridColumnReader
    {
        public static string[] ReadHeaders(IGridControl grid, int startColumn, int columnCount)
        {
            var headers = new string[columnCount];
            for (int c = 0; c < columnCount; c++)
            {
                int uiCol = startColumn + c;
                string header = null;
                try
                {
                    Bitmap _;
                    grid.GetHeaderInfo(uiCol, out header, out _);
                }
                catch
                {
                    header = null;
                }

                if (string.IsNullOrWhiteSpace(header))
                    header = $"Column_{uiCol}";

                headers[c] = header;
            }
            return headers;
        }
    }
}
