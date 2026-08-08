namespace XOABackupMonitorWeb.Models
{
    public class VMBackupStatus
    {
        public string VMName { get; set; } = string.Empty;
        public string InstanceName { get; set; } = string.Empty;
        public DateTime? LastBackupTime { get; set; }
        public BackupStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;

        // Total bytes across all currently-retained backups for this VM, and how
        // many are retained. Sourced from XOA's /rest/v0/backup-archives endpoint
        // (not backup-logs, which only covers execution history/status). Null
        // means the archive data couldn't be fetched/parsed for this VM - shown
        // as "N/A" rather than a misleading zero.
        public long? BackupSizeBytes { get; set; }
        public int? AvailableBackupsCount { get; set; }

        public double AgeInHours => LastBackupTime.HasValue
            ? (DateTime.Now - LastBackupTime.Value).TotalHours
            : 0;

        public string FormattedLastBackup => LastBackupTime?.ToString("g") ?? "Never";

        public string FormattedBackupSize => FormatBytes(BackupSizeBytes);

        public string StatusText => Status switch
        {
            BackupStatus.Success => "OK",
            BackupStatus.Warning => "Warning",
            BackupStatus.Failed => "Failed",
            BackupStatus.Error => "Error",
            _ => "Unknown"
        };

        public string StatusColor => Status switch
        {
            BackupStatus.Success => "#2ecc71",
            BackupStatus.Warning => "#f39c12",
            BackupStatus.Failed => "#e74c3c",
            BackupStatus.Error => "#9b59b6",
            _ => "#95a5a6"
        };

        public static string FormatBytes(long? bytes)
        {
            if (!bytes.HasValue) return "N/A";

            double b = bytes.Value;
            string[] units = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
            int unitIndex = 0;
            while (b >= 1024 && unitIndex < units.Length - 1)
            {
                b /= 1024;
                unitIndex++;
            }
            return $"{b:F2} {units[unitIndex]}";
        }
    }
}
