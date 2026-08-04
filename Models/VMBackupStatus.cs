namespace XOABackupMonitorWeb.Models
{
    public class VMBackupStatus
    {
        public string VMName { get; set; } = string.Empty;
        public string InstanceName { get; set; } = string.Empty;
        public DateTime? LastBackupTime { get; set; }
        public BackupStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;

        public double AgeInHours => LastBackupTime.HasValue
            ? (DateTime.Now - LastBackupTime.Value).TotalHours
            : 0;

        public string FormattedLastBackup => LastBackupTime?.ToString("g") ?? "Never";

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
    }
}
