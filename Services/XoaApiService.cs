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
        private const int MaxConcurrency = 5;

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
            string baseUrl, string apiToken, string instanceName, CancellationToken ct = default)
        {
            var client = CreateClient(apiToken);

            try
            {
                var vmUrls = await GetJsonArrayAsync(client, $"{baseUrl}/rest/v0/vms", ct);
                var vmResults = await MapWithConcurrencyAsync(vmUrls, MaxConcurrency,
                    vmUrl => GetJsonAsync<XoaVm>(client, $"{baseUrl}{vmUrl}", ct));

                var vms = vmResults
                    .Where(vm => vm != null && !vm.is_a_template && !vm.is_control_domain)
                    .Select(vm => vm!)
                    .GroupBy(v => v.uuid)
                    .Select(g => g.First())
                    .ToList();

                var hostUrls = await GetJsonArrayAsync(client, $"{baseUrl}/rest/v0/hosts", ct);
                var hostResults = await MapWithConcurrencyAsync(hostUrls, MaxConcurrency,
                    hostUrl => GetJsonAsync<XoaHost>(client, $"{baseUrl}{hostUrl}", ct));

                var hosts = new Dictionary<string, string>();
                foreach (var host in hostResults)
                {
                    if (host != null && !string.IsNullOrEmpty(host.uuid))
                    {
                        hosts[host.uuid] = host.name_label ?? "Unknown-Host";
                    }
                }

                _logger.LogInformation("[{Instance}] Found {Count} hosts: {Hosts}",
                    instanceName, hosts.Count, string.Join(", ", hosts.Values));

                var logUrls = await GetJsonArrayAsync(client, $"{baseUrl}/rest/v0/backup-logs", ct);
                var logResults = await MapWithConcurrencyAsync(logUrls, MaxConcurrency,
                    logUrl => GetJsonAsync<XoaBackupLog>(client, $"{baseUrl}{logUrl}", ct));

                var backupLogs = logResults.Where(l => l != null).Select(l => l!).ToList();

                return GenerateBackupReport(vms, backupLogs, hosts, instanceName);
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

        private List<VMBackupStatus> GenerateBackupReport(
            List<XoaVm> vms, List<XoaBackupLog> backupLogs, Dictionary<string, string> hosts, string instanceName)
        {
            var report = new List<VMBackupStatus>();
            var processedVMs = new HashSet<string>();
            var currentTime = DateTime.Now;

            foreach (var vm in vms)
            {
                if (processedVMs.Contains(vm.uuid ?? ""))
                    continue;

                processedVMs.Add(vm.uuid ?? "");

                string hostName = "Unknown-Host";
                if (!string.IsNullOrEmpty(vm.container) && hosts.ContainsKey(vm.container))
                {
                    hostName = hosts[vm.container];
                }

                string fullVmName = $"{hostName}-{vm.name_label ?? "Unknown"}";

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
                            var lastBackupTime = DateTimeOffset
                                .FromUnixTimeMilliseconds(vmTask.start)
                                .LocalDateTime;

                            var timeDiff = (currentTime - lastBackupTime).TotalHours;
                            BackupStatus status;
                            string statusText;
                            string messageDetail;

                            if (vmTask.status == "pending" || vmTask.status == "running")
                            {
                                status = BackupStatus.Warning;
                                statusText = "IN PROGRESS";
                                messageDetail = latestBackupLog.jobName ?? "N/A";
                            }
                            else if (IsCanceled(vmTask, out string cancelReason))
                            {
                                status = BackupStatus.Warning;
                                statusText = "CANCELED";
                                messageDetail = $"{latestBackupLog.jobName ?? "N/A"} - {(string.IsNullOrWhiteSpace(cancelReason) ? "Job canceled" : cancelReason)}";
                            }
                            else if (vmTask.status == "success" && timeDiff < 24)
                            {
                                status = BackupStatus.Success;
                                statusText = "SUCCESS";
                                messageDetail = latestBackupLog.jobName ?? "N/A";
                            }
                            else if (vmTask.status == "failure" || vmTask.status == "interrupted")
                            {
                                status = BackupStatus.Failed;
                                statusText = "FAILED";
                                messageDetail = latestBackupLog.jobName ?? "N/A";
                            }
                            else if (timeDiff >= 24)
                            {
                                status = BackupStatus.Warning;
                                statusText = "WARNING";
                                messageDetail = latestBackupLog.jobName ?? "N/A";
                            }
                            else
                            {
                                status = BackupStatus.Success;
                                statusText = "SUCCESS";
                                messageDetail = latestBackupLog.jobName ?? "N/A";
                            }

                            report.Add(new VMBackupStatus
                            {
                                InstanceName = instanceName,
                                VMName = fullVmName,
                                LastBackupTime = lastBackupTime,
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
    }
}
