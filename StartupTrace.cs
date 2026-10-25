using System;
using System.Diagnostics;
using System.IO;

namespace Atelier
{
    /// <summary>
    /// Opt-in startup timing: set ATELIER_STARTUP_TRACE to a file path and each launch
    /// appends milliseconds-since-process-start for the main milestones. Costs nothing
    /// when the variable is unset.
    /// </summary>
    internal static class StartupTrace
    {
        private static readonly string? FilePath = Environment.GetEnvironmentVariable("ATELIER_STARTUP_TRACE");

        public static void Mark(string what)
        {
            if (FilePath == null) return;
            var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            File.AppendAllText(FilePath, $"{ms,8:0} ms  {what}{Environment.NewLine}");
        }
    }
}
