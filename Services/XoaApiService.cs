using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using XOABackupMonitorWeb.Models;

namespace XOABackupMonitorWeb.Services
{
    public class XoaApiService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<XoaApiService> _logger;

        private const int DefaultMaxConcurrency = 12;

        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
        private readonly ConcurrentDictionary<string, (DateTime CachedAt, List<XoaVm> Vms, Dictionary<string, string> Hosts)> _vmsHostsCache = new();
        private readonly ConcurrentDictionary<string, (DateTime CachedAt, List<XoaBackupLog> Logs)> _backupLogsCache = new();
        private readonly ConcurrentDictionary<string, (DateTime CachedAt, List<XoaSchedule> Schedules)> _schedulesCache = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public XoaApiService(IHttpClientFactory httpClientFactory, ILogger<XoaApiService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public async Task<List<VMBackupStatus>> GetBackupStatusAsync(
            string baseUrl, string apiToken, string instanceName,
            int maxConcurrency = DefaultMaxConcurrency, CancellationToken ct = default)
        {
            var client = CreateClient(apiToken);

            try
            {
                var (vms, hosts) = await GetVmsAndHostsAsync(client, baseUrl, maxConcurrency, ct, allowCacheRead: false);
                var backupLogs = await GetBackupLogsAsync(client, baseUrl, maxConcurrency, ct, allowCacheRead: false);

                _logger.LogInformation("[{Instance}] Found {Count} hosts: {Hosts}",
                    instanceName, hosts.Count, string.Join(", ", hosts.Values));

                // Fetch each job's cron schedule(s) so staleness can be evaluated against
                // what the job is actually scheduled to do, instead of a flat 24-hour rule.
                // If this fails for any reason, fall back to an empty map - ClassifyTask
                // then reverts to the flat 24-hour check for every job, so a schedule-fetch
                // problem degrades gracefully instead of breaking the whole refresh.
                Dictionary<string, List<string>> jobSchedules;
                try
                {
                    var schedules = await GetSchedulesAsync(client, baseUrl, maxConcurrency, ct, allowCacheRead: false);
                    jobSchedules = BuildJobSchedulesMap(schedules);
                    _logger.LogInformation(
                        "[{Instance}] Loaded {EnabledCount} enabled schedule(s) covering {JobCount} job(s)",
                        instanceName, schedules.Count(s => s.enabled), jobSchedules.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[{Instance}] Failed to load backup schedules; falling back to flat 24-hour staleness check",
                        instanceName);
                    jobSchedules = new Dictionary<string, List<string>>();
                }

                return GenerateBackupReport(vms, backupLogs, hosts, instanceName, jobSchedules);
            }
            catch (HttpRequestException ex)
            {
                throw new HttpRequestException($"Unable to connect to server: {ex.Message}", ex);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "Server returned HTML instead of JSON. Check if the URL is correct and the API is enabled.", ex);
            }
        }

        public async Task<List<VmHistoryEntry>> GetVmHistoryAsync(
            string baseUrl, string apiToken, string vmFullName,
            int maxConcurrency = DefaultMaxConcurrency, CancellationToken ct = default)
        {
            var client = CreateClient(apiToken);

            var (vms, hosts) = await GetVmsAndHostsAsync(client, baseUrl, maxConcurrency, ct, allowCacheRead: true);

            XoaVm? targetVm = null;
            foreach (var vm in vms)
            {
                if (BuildFullVmName(vm, hosts) == vmFullName)
                {
                    targetVm = vm;
                    break;
                }
            }

            if (targetVm == null || string.IsNullOrEmpty(targetVm.uuid))
            {
                return new List<VmHistoryEntry>();
            }

            var backupLogs = await GetBackupLogsAsync(client, baseUrl, maxConcurrency, ct, allowCacheRead: true);
            var entries = new List<VmHistoryEntry>();

            foreach (var log in backupLogs)
            {
                var vmTask = log.tasks?.FirstOrDefault(t => t.data?.id == targetVm.uuid);
                if (vmTask == null)
                {
                    continue;
                }

                var (status, statusText, messageDetail) = ClassifyHistoricalTask(vmTask, log.jobName);

                if (statusText == "IN PROGRESS")
                {
                    continue;
                }

                entries.Add(new VmHistoryEntry
                {
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(vmTask.start).LocalDateTime,
                    Status = status,
                    StatusText = statusText,
                    Message = messageDetail
                });
            }

            return entries.OrderByDescending(e => e.Timestamp).ToList();
        }

        public async Task<bool> TestConnectionAsync(string baseUrl, string apiToken, CancellationToken ct = default)
        {
            try
            {
                var client = CreateClient(apiToken);
                var response = await client.GetAsync($"{baseUrl}/rest/v0/vms", ct);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private HttpClient CreateClient(string apiToken)
        {
            var client = _httpClientFactory.CreateClient("xoa");
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", $"authenticationToken={apiToken}");
            return client;
        }

        private async Task<(List<XoaVm> vms, Dictionary<string, string> hosts)> GetVmsAndHostsAsync(
            HttpClient client, string baseUrl, int maxConcurrency, CancellationToken ct, bool allowCacheRead)
        {
            if (allowCacheRead &&
                _vmsHostsCache.TryGetValue(baseUrl, out var cached) &&
                DateTime.UtcNow - cached.CachedAt < CacheTtl)
            {
                return (cached.Vms, cached.Hosts);
            }

            var vmUrls = await GetJsonArrayAsync(client, $"{baseUrl}/rest/v0/vms", ct);
            var vmResults = await MapWithConcurrencyAsync(vmUrls, maxConcurrency,
                vmUrl => GetJsonAsync<XoaVm>(client, $"{baseUrl}{vmUrl}", ct));

            var vms = vmResults
                .Where(vm => vm != null && !vm.is_a_template && !vm.is_control_domain)
                .Select(vm => vm!)
                .GroupBy(v => v.uuid)
                .Select(g => g.First())
                .ToList();

            var hostUrls = await GetJsonArrayAsync(client, $"{baseUrl}/rest/v0/hosts", ct);
            var hostResults = await MapWithConcurrencyAsync(hostUrls, maxConcurrency,
                hostUrl => GetJsonAsync<XoaHost>(client, $"{baseUrl}{hostUrl}", ct));

            var hosts = new Dictionary<string, string>();
            foreach (var host in hostResults)
            {
                if (host != null && !string.IsNullOrEmpty(host.uuid))
                {
                    hosts[host.uuid] = host.name_label ?? "Unknown-Host";
                }
            }

            _vmsHostsCache[baseUrl] = (DateTime.UtcNow, vms, hosts);
            return (vms, hosts);
        }

        private async Task<List<XoaBackupLog>> GetBackupLogsAsync(
            HttpClient client, string baseUrl, int maxConcurrency, CancellationToken ct, bool allowCacheRead)
        {
            if (allowCacheRead &&
                _backupLogsCache.TryGetValue(baseUrl, out var cached) &&
                DateTime.UtcNow - cached.CachedAt < CacheTtl)
            {
                return cached.Logs;
            }

            var logUrls = await GetJsonArrayAsync(client, $"{baseUrl}/rest/v0/backup-logs", ct);
            var logResults = await MapWithConcurrencyAsync(logUrls, maxConcurrency,
                logUrl => GetJsonAsync<XoaBackupLog>(client, $"{baseUrl}{logUrl}", ct));

            var logs = logResults.Where(l => l != null).Select(l => l!).ToList();

            _backupLogsCache[baseUrl] = (DateTime.UtcNow, logs);
            return logs;
        }

        /// <summary>
        /// Fetches each backup job's schedule(s) - a schedule carries the cron
        /// expression, the jobId it belongs to, and whether it's currently enabled.
        /// This is what lets ClassifyTask determine "was this job even supposed to
        /// run today" instead of assuming every job runs daily.
        /// </summary>
        private async Task<List<XoaSchedule>> GetSchedulesAsync(
            HttpClient client, string baseUrl, int maxConcurrency, CancellationToken ct, bool allowCacheRead)
        {
            if (allowCacheRead &&
                _schedulesCache.TryGetValue(baseUrl, out var cached) &&
                DateTime.UtcNow - cached.CachedAt < CacheTtl)
            {
                return cached.Schedules;
            }

            var scheduleUrls = await GetJsonArrayAsync(client, $"{baseUrl}/rest/v0/schedules", ct);
            var scheduleResults = await MapWithConcurrencyAsync(scheduleUrls, maxConcurrency,
                scheduleUrl => GetJsonAsync<XoaSchedule>(client, $"{baseUrl}{scheduleUrl}", ct));

            var schedules = scheduleResults.Where(s => s != null).Select(s => s!).ToList();

            _schedulesCache[baseUrl] = (DateTime.UtcNow, schedules);
            return schedules;
        }

        private static Dictionary<string, List<string>> BuildJobSchedulesMap(List<XoaSchedule> schedules)
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var s in schedules)
            {
                if (!s.enabled || string.IsNullOrEmpty(s.jobId) || string.IsNullOrEmpty(s.cron))
                {
                    continue;
                }

                if (!map.TryGetValue(s.jobId, out var list))
                {
                    list = new List<string>();
                    map[s.jobId] = list;
                }
                list.Add(s.cron);
            }
            return map;
        }

        private static string BuildFullVmName(XoaVm vm, Dictionary<string, string> hosts)
        {
            string hostName = "Unknown-Host";
            if (!string.IsNullOrEmpty(vm.container) && hosts.ContainsKey(vm.container))
            {
                hostName = hosts[vm.container];
            }
            return $"{hostName}-{vm.name_label ?? "Unknown"}";
        }

        private static bool IsCanceled(XoaBackupTask task, out string cancelReason)
        {
            cancelReason = string.Empty;

            if (!string.IsNullOrWhiteSpace(task.message) &&
                task.message.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                cancelReason = task.message;
                return true;
            }

            if (task.tasks != null)
            {
                foreach (var sub in task.tasks)
                {
                    if (!string.IsNullOrWhiteSpace(sub.message) &&
                        sub.message.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        cancelReason = sub.message;
                        return true;
                    }

                    if (sub.tasks != null)
                    {
                        foreach (var leaf in sub.tasks)
                        {
                            if (!string.IsNullOrWhiteSpace(leaf.message) &&
                                leaf.message.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                cancelReason = leaf.message;
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether a completed ("success") backup is stale relative to its
        /// OWN job schedule, instead of a flat 24-hour rule. This is the fix for jobs
        /// that intentionally skip certain days (e.g. no fresh backups on Sunday to let
        /// things coalesce): if the job's cron excludes Sunday, the "most recent expected
        /// occurrence" on a Monday check correctly resolves to Saturday, not Sunday, so a
        /// Saturday backup showing as 30+ hours old is NOT flagged as stale. Jobs with a
        /// different schedule (e.g. ones that DO run Sunday) are evaluated against their
        /// own cron independently, so this isn't a blanket "skip Sunday" rule - each job
        /// is judged only against what it is actually configured to do.
        ///
        /// Falls back to the flat 24-hour check if no enabled schedule can be resolved
        /// for the job (unmapped jobId, missing/disabled schedule, cron fetch failure).
        /// </summary>
        private static bool IsBackupStaleRelativeToSchedule(
            string? jobId, Dictionary<string, List<string>> jobSchedules,
            DateTime lastBackupTime, DateTime currentTime, out DateTime? expectedSince)
        {
            expectedSince = null;

            if (!string.IsNullOrEmpty(jobId) && jobSchedules.TryGetValue(jobId, out var crons) && crons.Count > 0)
            {
                DateTime? mostRecentExpected = null;
                foreach (var cron in crons)
                {
                    var occurrence = CronScheduleEvaluator.GetLastOccurrenceOnOrBefore(cron, currentTime);
                    if (occurrence.HasValue && (mostRecentExpected == null || occurrence.Value > mostRecentExpected.Value))
                    {
                        mostRecentExpected = occurrence.Value;
                    }
                }

                if (mostRecentExpected.HasValue)
                {
                    expectedSince = mostRecentExpected;
                    bool missedExpectedRun = lastBackupTime < mostRecentExpected.Value;
                    // 2-hour grace window: avoids flagging a job as stale in the few
                    // minutes/hours right after its scheduled time, before it's had a
                    // realistic chance to kick off and complete.
                    bool pastGracePeriod = (currentTime - mostRecentExpected.Value) > TimeSpan.FromHours(2);
                    return missedExpectedRun && pastGracePeriod;
                }
            }

            return (currentTime - lastBackupTime).TotalHours >= 24;
        }

        private static (BackupStatus Status, string StatusText, string MessageDetail, double TimeDiffHours) ClassifyTask(
            XoaBackupTask vmTask, string? jobName, string? jobId,
            Dictionary<string, List<string>> jobSchedules, DateTime currentTime)
        {
            var lastBackupTime = DateTimeOffset.FromUnixTimeMilliseconds(vmTask.start).LocalDateTime;
            var timeDiff = (currentTime - lastBackupTime).TotalHours;
            BackupStatus status;
            string statusText;
            string messageDetail;

            if (vmTask.status == "pending" || vmTask.status == "running")
            {
                status = BackupStatus.Warning;
                statusText = "IN PROGRESS";
                messageDetail = jobName ?? "N/A";
            }
            else if (IsCanceled(vmTask, out string cancelReason))
            {
                status = BackupStatus.Warning;
                statusText = "CANCELED";
                messageDetail = $"{jobName ?? "N/A"} - {(string.IsNullOrWhiteSpace(cancelReason) ? "Job canceled" : cancelReason)}";
            }
            else if (vmTask.status == "failure" || vmTask.status == "interrupted")
            {
                status = BackupStatus.Failed;
                statusText = "FAILED";
                messageDetail = jobName ?? "N/A";
            }
            else if (vmTask.status == "success")
            {
                bool isStale = IsBackupStaleRelativeToSchedule(jobId, jobSchedules, lastBackupTime, currentTime, out DateTime? expectedSince);

                if (!isStale)
                {
                    status = BackupStatus.Success;
                    statusText = "SUCCESS";
                    messageDetail = jobName ?? "N/A";
                }
                else
                {
                    status = BackupStatus.Warning;
                    statusText = "WARNING";
                    messageDetail = expectedSince.HasValue
                        ? $"{jobName ?? "N/A"} - expected by {expectedSince.Value:g} per job schedule"
                        : jobName ?? "N/A";
                }
            }
            else
            {
                status = timeDiff >= 24 ? BackupStatus.Warning : BackupStatus.Success;
                statusText = timeDiff >= 24 ? "WARNING" : "SUCCESS";
                messageDetail = jobName ?? "N/A";
            }

            return (status, statusText, messageDetail, timeDiff);
        }

        private static (BackupStatus Status, string StatusText, string MessageDetail) ClassifyHistoricalTask(
            XoaBackupTask vmTask, string? jobName)
        {
            if (vmTask.status == "pending" || vmTask.status == "running")
            {
                return (BackupStatus.Warning, "IN PROGRESS", jobName ?? "N/A");
            }

            if (IsCanceled(vmTask, out string cancelReason))
            {
                var msg = $"{jobName ?? "N/A"} - {(string.IsNullOrWhiteSpace(cancelReason) ? "Job canceled" : cancelReason)}";
                return (BackupStatus.Warning, "CANCELED", msg);
            }

            if (vmTask.status == "success")
            {
                return (BackupStatus.Success, "SUCCESS", jobName ?? "N/A");
            }

            if (vmTask.status == "failure" || vmTask.status == "interrupted")
            {
                return (BackupStatus.Failed, "FAILED", jobName ?? "N/A");
            }

            return (BackupStatus.Unknown, "UNKNOWN", jobName ?? "N/A");
        }

        private List<VMBackupStatus> GenerateBackupReport(
            List<XoaVm> vms, List<XoaBackupLog> backupLogs, Dictionary<string, string> hosts,
            string instanceName, Dictionary<string, List<string>> jobSchedules)
        {
            var report = new List<VMBackupStatus>();
            var processedVMs = new HashSet<string>();
            var currentTime = DateTime.Now;

            foreach (var vm in vms)
            {
                if (processedVMs.Contains(vm.uuid ?? ""))
                    continue;

                processedVMs.Add(vm.uuid ?? "");

                string fullVmName = BuildFullVmName(vm, hosts);

                var vmBackups = backupLogs
                    .Where(log => log.tasks != null && log.tasks.Count > 0 &&
                                  log.tasks.Any(t => t.data?.id == vm.uuid))
                    .OrderByDescending(log => log.start)
                    .ToList();

                if (vmBackups.Any())
                {
                    var latestBackupLog = vmBackups.First();
                    var vmTaskList = latestBackupLog.tasks;

                    if (vmTaskList != null && vmTaskList.Any())
                    {
                        var vmTask = vmTaskList.FirstOrDefault(t => t.data?.id == vm.uuid);

                        if (vmTask != null)
                        {
                            var (status, statusText, messageDetail, timeDiff) =
                                ClassifyTask(vmTask, latestBackupLog.jobName, latestBackupLog.jobId, jobSchedules, currentTime);

                            report.Add(new VMBackupStatus
                            {
                                InstanceName = instanceName,
                                VMName = fullVmName,
                                LastBackupTime = DateTimeOffset.FromUnixTimeMilliseconds(vmTask.start).LocalDateTime,
                                Status = status,
                                Message = $"{statusText} - {messageDetail} ({Math.Round(timeDiff, 1)} hours ago)"
                            });
                        }
                    }
                }
                else
                {
                    report.Add(new VMBackupStatus
                    {
                        InstanceName = instanceName,
                        VMName = fullVmName,
                        LastBackupTime = null,
                        Status = BackupStatus.Failed,
                        Message = "NO BACKUP - Never backed up"
                    });
                }
            }

            return report;
        }

        private static async Task<List<TResult>> MapWithConcurrencyAsync<TSource, TResult>(
            IEnumerable<TSource> source, int maxConcurrency, Func<TSource, Task<TResult>> selector)
        {
            using var semaphore = new SemaphoreSlim(maxConcurrency);
            var tasks = source.Select(async item =>
            {
                await semaphore.WaitAsync();
                try
                {
                    return await selector(item);
                }
                finally
                {
                    semaphore.Release();
                }
            });
            return (await Task.WhenAll(tasks)).ToList();
        }

        private static async Task<T?> GetJsonAsync<T>(HttpClient client, string url, CancellationToken ct)
        {
            var response = await client.GetAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new UnauthorizedAccessException("Invalid API token");
            }

            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(ct);

            if (content.TrimStart().StartsWith("<"))
            {
                throw new JsonException("Server returned HTML instead of JSON");
            }

            return JsonSerializer.Deserialize<T>(content, JsonOptions);
        }

        private static async Task<List<string>> GetJsonArrayAsync(HttpClient client, string url, CancellationToken ct)
        {
            var response = await client.GetAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new UnauthorizedAccessException("Invalid API token");
            }

            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(ct);

            if (content.TrimStart().StartsWith("<"))
            {
                throw new JsonException("Server returned HTML instead of JSON");
            }

            return JsonSerializer.Deserialize<List<string>>(content, JsonOptions) ?? new List<string>();
        }

        /// <summary>
        /// Minimal standard 5-field cron evaluator (minute hour day-of-month month
        /// day-of-week). Supports *, single values, comma lists, ranges (a-b), and
        /// steps (*/n or a-b/n) per field - covers the vast majority of real-world
        /// backup job schedules without pulling in an external cron library.
        /// </summary>
        private static class CronScheduleEvaluator
        {
            public static DateTime? GetLastOccurrenceOnOrBefore(
                string cronExpression, DateTime reference, int maxLookbackDays = 60)
            {
                if (string.IsNullOrWhiteSpace(cronExpression))
                {
                    return null;
                }

                var parts = cronExpression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5)
                {
                    return null;
                }

                var minuteField = ParseField(parts[0], 0, 59);
                var hourField = ParseField(parts[1], 0, 23);
                var domField = ParseField(parts[2], 1, 31);
                var monthField = ParseField(parts[3], 1, 12);
                var dowField = ParseField(parts[4], 0, 7); // 0 and 7 both mean Sunday

                if (minuteField == null || hourField == null || domField == null ||
                    monthField == null || dowField == null)
                {
                    return null;
                }

                for (int dayOffset = 0; dayOffset <= maxLookbackDays; dayOffset++)
                {
                    var candidateDate = reference.Date.AddDays(-dayOffset);

                    bool domMatch = domField.Contains(candidateDate.Day);
                    bool monthMatch = monthField.Contains(candidateDate.Month);
                    int dow = (int)candidateDate.DayOfWeek; // 0=Sunday..6=Saturday
                    bool dowMatch = dowField.Contains(dow) || (dow == 0 && dowField.Contains(7));

                    if (!domMatch || !monthMatch || !dowMatch)
                    {
                        continue;
                    }

                    DateTime? best = null;
                    foreach (var h in hourField)
                    {
                        foreach (var m in minuteField)
                        {
                            var candidate = candidateDate.AddHours(h).AddMinutes(m);
                            if (candidate > reference)
                            {
                                continue;
                            }
                            if (best == null || candidate > best)
                            {
                                best = candidate;
                            }
                        }
                    }

                    if (best != null)
                    {
                        return best;
                    }
                    // No matching time-of-day found on this otherwise-matching day
                    // (e.g. reference is earlier today than the scheduled time) -
                    // keep walking further back.
                }

                return null;
            }

            private static HashSet<int>? ParseField(string field, int min, int max)
            {
                var result = new HashSet<int>();
                try
                {
                    foreach (var token in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string rangeAndStep = token;
                        int step = 1;

                        if (rangeAndStep.Contains('/'))
                        {
                            var stepParts = rangeAndStep.Split('/');
                            rangeAndStep = stepParts[0];
                            step = int.Parse(stepParts[1]);
                        }

                        int rangeStart, rangeEnd;
                        if (rangeAndStep == "*")
                        {
                            rangeStart = min;
                            rangeEnd = max;
                        }
                        else if (rangeAndStep.Contains('-'))
                        {
                            var bounds = rangeAndStep.Split('-');
                            rangeStart = int.Parse(bounds[0]);
                            rangeEnd = int.Parse(bounds[1]);
                        }
                        else
                        {
                            rangeStart = rangeEnd = int.Parse(rangeAndStep);
                        }

                        for (int v = rangeStart; v <= rangeEnd; v += step)
                        {
                            if (v >= min && v <= max)
                            {
                                result.Add(v);
                            }
                        }
                    }
                }
                catch
                {
                    return null;
                }

                return result.Count > 0 ? result : null;
            }
        }

        private class XoaVm
        {
            public string? uuid { get; set; }
            public string? name_label { get; set; }
            public bool is_a_template { get; set; }
            public bool is_control_domain { get; set; }

            [JsonPropertyName("$container")]
            public string? container { get; set; }
        }

        private class XoaHost
        {
            public string? uuid { get; set; }
            public string? name_label { get; set; }
        }

        private class XoaBackupLog
        {
            public string? jobId { get; set; }
            public string? jobName { get; set; }
            public long start { get; set; }
            public List<XoaBackupTask>? tasks { get; set; }
        }

        private class XoaBackupTask
        {
            public long start { get; set; }
            public string? status { get; set; }
            public string? message { get; set; }
            public XoaTaskData? data { get; set; }
            public List<XoaBackupTask>? tasks { get; set; }
        }

        private class XoaTaskData
        {
            public string? id { get; set; }
        }

        private class XoaSchedule
        {
            public string? id { get; set; }
            public string? cron { get; set; }
            public bool enabled { get; set; } = true;
            public string? jobId { get; set; }
        }
    }
}
