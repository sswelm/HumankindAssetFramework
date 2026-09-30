// The one thing BackupDedup takes from the Unity window: the byte formatter in its report. Everything else it needs is
// System.IO and kernel32 (CreateHardLink), which run as-is on Unity's Mono.
static class BackupWindow
{
    internal static string Human(long bytes) => bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.0") + " KB" : (bytes / 1048576.0).ToString("0.0") + " MB";
}
