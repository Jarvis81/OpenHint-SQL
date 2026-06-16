using System;

namespace OpenHintSQL.Commands
{
    /// <summary>
    /// GUIDs and IDs shared between the .vsct command table and the managed
    /// command handler registration. Keep in sync with PackageCommands.vsct.
    /// </summary>
    internal static class PackageCommandGuids
    {
        /// <summary>
        /// Command set GUID for all OpenHint SQL commands. Mirrors
        /// guidOpenHintSqlCmdSet in PackageCommands.vsct.
        /// </summary>
        public static readonly Guid CmdSet = new Guid("7B5A2E1C-3D4F-4F6A-9B8C-1E2D3C4B5A6F");

        /// <summary>
        /// Command id for "Generate as INSERT script". Triggered both by the
        /// VSCT-registered Tools menu entry and by the result-grid context-menu
        /// item injected at runtime.
        /// </summary>
        public const int GenerateInsertScriptCmdId = 0x0100;
    }
}
