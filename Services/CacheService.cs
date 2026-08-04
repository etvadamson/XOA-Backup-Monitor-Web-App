using System.Text.Json;
using XOABackupMonitorWeb.Models;

namespace XOABackupMonitorWeb.Services
{
    public class CacheService
    {
        private readonly string _cacheFilePath;
        private readonly ILogger<CacheService> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public CacheService(IWebHostEnvironment env, ILogger<CacheService> logger)
        {
            _logger = logger;
            var dataDir = Path.Combine(env.ContentRootPath, "data");
            Directory.CreateDirectory(dataDir);
            _cacheFilePath = Path.Combine(dataDir, "backup_cache.json");
        }

        public class CacheData
        {
            public DateTime LastUpdate { get; set; }
            public List<VMBackupStatus> Backups { get; set; } = new();
        }

        public async Task SaveCacheAsync(List<VMBackupStatus> backups)
        {
            await _lock.WaitAsync();
            try
            {
                var cache = new CacheData { LastUpdate = DateTime.Now, Backups = backups };
                var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_cacheFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to save cache");
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<CacheData?> LoadCacheAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (!File.Exists(_cacheFilePath))
                {
                    return null;
                }

                var json = await File.ReadAllTextAsync(_cacheFilePath);
                return JsonSerializer.Deserialize<CacheData>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load cache");
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
