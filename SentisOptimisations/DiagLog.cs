namespace SentisOptimisations
{
    /// <summary>
    /// Whether the plugin's diagnostic lines go to the log (<c>Diagnostic logs</c> on the Logs tab, off by default):
    /// timings, warm-ups, what an optimisation did. Errors and real problems are logged whatever it says.
    /// </summary>
    public static class DiagLog
    {
        public static bool On => SentisOptimisationsPlugin.SentisOptimisationsPlugin.Config?.DiagnosticLogs == true;
    }
}
