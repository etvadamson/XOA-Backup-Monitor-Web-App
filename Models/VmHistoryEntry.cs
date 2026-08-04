namespace XOABackupMonitorWeb.Models
{
    public class VmHistoryEntry
    {
        public DateTime Timestamp { get; set; }
        public BackupStatus Status { get; set; }
        public string StatusText { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
