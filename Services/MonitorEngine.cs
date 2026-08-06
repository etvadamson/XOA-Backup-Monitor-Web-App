using System.Text;
using XOABackupMonitorWeb.Models;

namespace XOABackupMonitorWeb.Services
{
    public class MonitorEngine
    {
        private readonly XoaApiService _apiService;
        private readonly ConfigService _configService;
        private readonly CacheService _cacheService;
        private readonly ILogger<MonitorEngine> _logger;
        private readonly object _stateLock = new();

        private List<VMBackupStatus> _currentBackups = new();
        private DateTime? _lastRefresh;

        public MonitorEngine(
            XoaApiService apiService,
            ConfigService configService,
            CacheService cacheService,
            ILogger<MonitorEngine> logger)
        {
            _apiService = apiService;
            _configService = configService;
            _cacheService = cacheService;
            _logger = logger;
        }

        public async Task LoadFromCacheAsync()
        {
            var cache = await _cacheService.LoadCacheAsync();
            if (cache != null)
            {
                lock (_stateLock)
                {
                    _currentBackups = cache.Backups;
                    _lastRefresh = cache.LastUpdate;
                }
            }
        }

        public object GetStatusSnapshot(Dictionary<string, string>? instanceUrls = null)
        {
            instanceUrls ??= new Dictionary<string, string>();

            lock (_stateLock)
            {
                var grouped = _currentBackups
                    .GroupBy(b => b.InstanceName)
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        instanceName = g.Key,
                        instanceUrl = instanceUrls.TryGetValue(g.Key ?? "", out var url) ? url : "",
                        summary = BuildSummary(g.ToList()),
                        statusText = BuildGroupStatusText(g.ToList()),
                        statusColor = BuildGroupStatusColor(g.ToList()),
                        vms = g.OrderBy(v => v.VMName).ToList()
                    })
                    .ToList();

                return new
                {
                    lastRefresh = _lastRefresh,
                    overallStatus = BuildOverallStatus(_currentBackups),
                    totalVmCount = _currentBackups.Count,
                    summary = BuildSummary(_currentBackups),
                    groups = grouped
                };
            }
        }

        public async Task RefreshAllAsync(CancellationToken ct = default)
        {
            var instances = await _configService.LoadInstancesAsync();
            var maxConcurrentRequests = await _configService.GetMaxConcurrentRequestsAsync();

            var tasks = instances.Where(i => i.IsEnabled)
                .Select(instance => RefreshSingleInstanceInternalAsync(instance, maxConcurrentRequests, ct));

            var results = await Task.WhenAll(tasks);

            var tempBackups = new List<VMBackupStatus>();
            foreach (var r in results)
            {
                tempBackups.AddRange(r);
            }

            var deduplicated = tempBackups
                .GroupBy(b => new { b.InstanceName, b.VMName })
                .Select(g => g.OrderByDescending(b => b.LastBackupTime ?? DateTime.MinValue).First())
                .ToList();

            lock (_stateLock)
            {
                _currentBackups = deduplicated;
                _lastRefresh = DateTime.Now;
            }

            await _cacheService.SaveCacheAsync(deduplicated);
        }

        public async Task RefreshInstanceAsync(string instanceName, CancellationToken ct = default)
        {
            var instances = await _configService.LoadInstancesAsync();
            var instance = instances.FirstOrDefault(i => i.Name == instanceName);
            if (instance == null || !instance.IsEnabled)
            {
                return;
            }

            var maxConcurrentRequests = await _configService.GetMaxConcurrentRequestsAsync();
            var results = await RefreshSingleInstanceInternalAsync(instance, maxConcurrentRequests, ct);

            List<VMBackupStatus> snapshot;
            lock (_stateLock)
            {
                _currentBackups.RemoveAll(b => b.InstanceName == instanceName);
                _currentBackups.AddRange(results);
                _lastRefresh = DateTime.Now;
                snapshot = new List<VMBackupStatus>(_currentBackups);
            }

            await _cacheService.SaveCacheAsync(snapshot);
        }

        public async Task RemoveInstanceAsync(string instanceName)
        {
            List<VMBackupStatus> snapshot;
            lock (_stateLock)
            {
                _currentBackups.RemoveAll(b => b.InstanceName == instanceName);
                snapshot = new List<VMBackupStatus>(_currentBackups);
            }

            await _cacheService.SaveCacheAsync(snapshot);
        }

        private async Task<List<VMBackupStatus>> RefreshSingleInstanceInternalAsync(
            XOAInstance instance, int maxConcurrentRequests, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(instance.ApiToken))
            {
                return new List<VMBackupStatus>
                {
                    new VMBackupStatus
                    {
                        InstanceName = instance.Name,
                        VMName = "Configuration Error",
                        Status = BackupStatus.Error,
                        Message = "No API token configured. Add one via Configure.",
                        LastBackupTime = null
                    }
                };
            }

            try
            {
                return await _apiService.GetBackupStatusAsync(instance.Url, instance.ApiToken, instance.Name, maxConcurrentRequests, ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Connection error for {Instance}", instance.Name);
                return new List<VMBackupStatus>
                {
                    new VMBackupStatus
                    {
                        InstanceName = instance.Name,
                        VMName = "Connection Error",
                        Status = BackupStatus.Error,
                        Message = $"Unable to reach server: {ex.Message}",
                        LastBackupTime = null
                    }
                };
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning("Auth error for {Instance}: invalid API token", instance.Name);
                return new List<VMBackupStatus>
                {
                    new VMBackupStatus
                    {
                        InstanceName = instance.Name,
                        VMName = "Authentication Error",
                        Status = BackupStatus.Error,
                        Message = "Invalid API token. Check credentials in configuration.",
                        LastBackupTime = null
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching from {Instance}", instance.Name);
                return new List<VMBackupStatus>
                {
                    new VMBackupStatus
                    {
                        InstanceName = instance.Name,
                        VMName = "Error",
                        Status = BackupStatus.Error,
                        Message = $"Error: {ex.Message}",
                        LastBackupTime = null
                    }
                };
            }
        }

        public string ExportToCsv()
        {
            lock (_stateLock)
            {
                var csv = new StringBuilder();
                csv.AppendLine("Instance,VMName,Status,LastBackup,HoursAgo,Message");

                foreach (var backup in _currentBackups.OrderBy(b => b.InstanceName).ThenBy(b => b.VMName))
                {
                    csv.AppendLine(
                        $"\"{backup.InstanceName}\",\"{backup.VMName}\"," +
                        $"\"{backup.StatusText}\",\"{backup.FormattedLastBackup}\"," +
                        $"{backup.AgeInHours:F1},\"{backup.Message}\"");
                }

                return csv.ToString();
            }
        }

        private static string BuildSummary(List<VMBackupStatus> backups)
        {
            if (!backups.Any()) return "No data";

            var success = backups.Count(b => b.Status == BackupStatus.Success);
            var warning = backups.Count(b => b.Status == BackupStatus.Warning);
            var failed = backups.Count(b => b.Status == BackupStatus.Failed);
            var error = backups.Count(b => b.Status == BackupStatus.Error);

            return $"Total: {backups.Count} | Success: {success} | Warning: {warning} | Failed: {failed} | Errors: {error}";
        }

        private static string BuildGroupStatusText(List<VMBackupStatus> vms)
        {
            if (!vms.Any()) return "NO DATA";
            if (vms.Any(v => v.Status == BackupStatus.Error)) return "CONNECTION ERROR";
            if (vms.Any(v => v.Status == BackupStatus.Failed)) return "FAILURES DETECTED";
            if (vms.Any(v => v.Status == BackupStatus.Warning)) return "WARNINGS";
            return "ALL OK";
        }

        private static string BuildGroupStatusColor(List<VMBackupStatus> vms)
        {
            if (!vms.Any()) return "#95a5a6";
            if (vms.Any(v => v.Status == BackupStatus.Error)) return "#9b59b6";
            if (vms.Any(v => v.Status == BackupStatus.Failed)) return "#e74c3c";
            if (vms.Any(v => v.Status == BackupStatus.Warning)) return "#f39c12";
            return "#2ecc71";
        }

        private static string BuildOverallStatus(List<VMBackupStatus> backups)
        {
            if (!backups.Any()) return "Unknown";
            if (backups.Any(b => b.Status == BackupStatus.Error)) return "Error";
            if (backups.Any(b => b.Status == BackupStatus.Failed)) return "Failed";
            if (backups.Any(b => b.Status == BackupStatus.Warning)) return "Warning";
            return "Success";
        }
    }
}
