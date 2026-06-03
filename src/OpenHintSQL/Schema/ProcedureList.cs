using System;
using System.Collections.Generic;

namespace OpenHintSQL.Schema
{
    internal sealed class ProcedureList
    {
        public List<ProcedureInfo> Items { get; } = new List<ProcedureInfo>();

        public bool IsLoaded { get; set; }

        public DateTime LoadedAt { get; set; }

        public string LoadError { get; set; }

        public static ProcedureList Empty
        {
            get { return new ProcedureList { IsLoaded = false }; }
        }
    }
}
