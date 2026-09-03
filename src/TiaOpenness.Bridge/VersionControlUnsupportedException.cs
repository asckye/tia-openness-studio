using System;

namespace TiaOpenness.Bridge
{
    /// <summary>
    /// The open project exposes no Version Control Interface. VCI arrived in TIA Portal V21, so
    /// this is a capability gap on older installations rather than a failure, and it carries its
    /// own RPC code so a front end can hide the feature instead of reporting an error.
    /// </summary>
    public class VersionControlUnsupportedException : Exception
    {
        public VersionControlUnsupportedException()
            : base("This project exposes no Version Control Interface. VCI requires TIA Portal V21 or later; " +
                   "on an older version use 'export --format Source' for a text snapshot instead.")
        {
        }
    }
}
