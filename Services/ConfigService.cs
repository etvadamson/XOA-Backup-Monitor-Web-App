using System.Security.Cryptography;
using System.Text.Json;
using XOABackupMonitorWeb.Models;

namespace XOABackupMonitorWeb.Services
{
    public class ConfigService
    {
        private readonly string _dataDir;
        private readonly string _configFilePath;
        private readonly string _settingsFilePath;
        private readonly string _keyFilePath;
        private readonly ILogger<ConfigService> _logger;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private const int DefaultRefreshIntervalMinutes = 30;
        private const int DefaultMaxConcurrentRequests = 12;
        private const int DefaultMaxConcurrentInstanceRefreshes = 5;

        public ConfigService(IWebHostEnvironment env, ILogger<ConfigService> logger)
        {
            _logger = logger;
            _dataDir = Path.Combine(env.ContentRootPath, "data");
            Directory.CreateDirectory(_dataDir);

            _configFilePath = Path.Combine(_dataDir, "xoa_config.dat");
            _settingsFilePath = Path.Combine(_dataDir, "settings.json");
            _keyFilePath = Path.Combine(_dataDir, "encryption.key");
        }

        public class StoredConfig
        {
            public List<XOAInstance> Instances { get; set; } = new();
        }

        public class StoredSettings
        {
            public int RefreshInterval { get; set; } = DefaultRefreshIntervalMinutes;
            public int MaxConcurrentRequests { get; set; } = DefaultMaxConcurrentRequests;
            public int MaxConcurrentInstanceRefreshes { get; set; } = DefaultMaxConcurrentInstanceRefreshes;
        }

        public async Task<List<XOAInstance>> LoadInstancesAsync()
        {
            await _lock.WaitAsync();
            try
            {
                return LoadInternal().Instances;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<List<XOAInstanceSummary>> LoadInstanceSummariesAsync()
        {
            var instances = await LoadInstancesAsync();
            return instances.Select(i => new XOAInstanceSummary
            {
                Id = i.Id,
                Name = i.Name,
                Url = i.Url,
                IsEnabled = i.IsEnabled,
                HasToken = !string.IsNullOrEmpty(i.ApiToken)
            }).ToList();
        }

        /// <summary>
        /// Result of a create attempt. Success is false with a Reason when the
        /// instance was NOT created (e.g. a name collision).
        /// </summary>
        public record CreateInstanceResult(bool Success, string? Reason = null);

        /// <summary>
        /// Creates a brand-new instance. This is what POST /api/instances (the
        /// "blank form, new instance" flow) now uses.
        ///
        /// FIX: previously this method (formerly UpsertInstanceAsync) matched an
        /// "existing" instance purely by Name and MERGED into it - silently
        /// overwriting that other instance's URL/enabled/token if the name
        /// happened to collide (including via trailing whitespace from
        /// copy/paste, which is easy to hit once you have 50-100 instances).
        /// The visible symptom: "Test Connection" against the NEW url/token
        /// succeeds (it always tests exactly what's typed), but the dashboard
        /// later shows "Invalid API token" for that instance - because what
        /// actually got saved/refreshed was the OTHER, pre-existing instance
        /// under the same name, with a token that didn't necessarily match.
        ///
        /// Now: creation always assigns a fresh Id and REJECTS (returns
        /// Success=false) if the name is already used by a different instance,
        /// instead of silently merging into it. Editing an existing instance by
        /// name is only possible via UpdateInstanceByIdAsync (the click-to-edit
        /// flow), which is unaffected by this change.
        /// </summary>
        public async Task<CreateInstanceResult> CreateInstanceAsync(XOAInstance instance)
        {
            await _lock.WaitAsync();
            try
            {
                var config = LoadInternal();
                var trimmedName = instance.Name.Trim();

                var collision = config.Instances.FirstOrDefault(i =>
                    string.Equals(i.Name.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase));

                if (collision != null)
                {
                    return new CreateInstanceResult(false,
                        $"An instance named \"{collision.Name}\" already exists. Use a unique name, or click that instance in the list above to edit it instead.");
                }

                instance.Name = trimmedName;
                instance.Id = Guid.NewGuid().ToString("N");
                config.Instances.Add(instance);
                SaveInternal(config);

                return new CreateInstanceResult(true);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<bool> UpdateInstanceByIdAsync(string id, XOAInstance instance)
        {
            await _lock.WaitAsync();
            try
            {
                var config = LoadInternal();
                var existing = config.Instances.FirstOrDefault(i => i.Id == id);
                if (existing == null)
                {
                    return false;
                }

                existing.Name = instance.Name.Trim();
                existing.Url = instance.Url;
                existing.IsEnabled = instance.IsEnabled;
                if (!string.IsNullOrEmpty(instance.ApiToken))
                {
                    existing.ApiToken = instance.ApiToken;
                }

                SaveInternal(config);
                return true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<bool?> ToggleInstanceEnabledAsync(string id)
        {
            await _lock.WaitAsync();
            try
            {
                var config = LoadInternal();
                var existing = config.Instances.FirstOrDefault(i => i.Id == id);
                if (existing == null)
                {
                    return null;
                }

                existing.IsEnabled = !existing.IsEnabled;
                SaveInternal(config);
                return existing.IsEnabled;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task DeleteInstanceAsync(string name)
        {
            await _lock.WaitAsync();
            try
            {
                var config = LoadInternal();
                config.Instances.RemoveAll(i => i.Name == name);
                SaveInternal(config);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<int> GetGlobalRefreshIntervalAsync()
        {
            var settings = await LoadSettingsInternalAsync();
            return settings.RefreshInterval;
        }

        public async Task SetGlobalRefreshIntervalAsync(int minutes)
        {
            await _lock.WaitAsync();
            try
            {
                var settings = LoadSettingsInternal();
                settings.RefreshInterval = minutes;
                SaveSettingsInternal(settings);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<int> GetMaxConcurrentRequestsAsync()
        {
            var settings = await LoadSettingsInternalAsync();
            return settings.MaxConcurrentRequests;
        }

        public async Task SetMaxConcurrentRequestsAsync(int maxConcurrentRequests)
        {
            await _lock.WaitAsync();
            try
            {
                var settings = LoadSettingsInternal();
                settings.MaxConcurrentRequests = maxConcurrentRequests;
                SaveSettingsInternal(settings);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<int> GetMaxConcurrentInstanceRefreshesAsync()
        {
            var settings = await LoadSettingsInternalAsync();
            return settings.MaxConcurrentInstanceRefreshes;
        }

        public async Task SetMaxConcurrentInstanceRefreshesAsync(int maxConcurrentInstanceRefreshes)
        {
            await _lock.WaitAsync();
            try
            {
                var settings = LoadSettingsInternal();
                settings.MaxConcurrentInstanceRefreshes = maxConcurrentInstanceRefreshes;
                SaveSettingsInternal(settings);
            }
            finally
            {
                _lock.Release();
            }
        }

        private async Task<StoredSettings> LoadSettingsInternalAsync()
        {
            await _lock.WaitAsync();
            try
            {
                return LoadSettingsInternal();
            }
            finally
            {
                _lock.Release();
            }
        }

        private StoredSettings LoadSettingsInternal()
        {
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    return new StoredSettings();
                }

                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<StoredSettings>(json);
                if (settings != null)
                {
                    if (settings.RefreshInterval < 1) settings.RefreshInterval = DefaultRefreshIntervalMinutes;
                    if (settings.MaxConcurrentRequests < 1) settings.MaxConcurrentRequests = DefaultMaxConcurrentRequests;
                    if (settings.MaxConcurrentInstanceRefreshes < 1) settings.MaxConcurrentInstanceRefreshes = DefaultMaxConcurrentInstanceRefreshes;
                    return settings;
                }

                return new StoredSettings();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read settings, using defaults");
                return new StoredSettings();
            }
        }

        private void SaveSettingsInternal(StoredSettings settings)
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
        }

        private StoredConfig LoadInternal()
        {
            try
            {
                if (!File.Exists(_configFilePath))
                {
                    return new StoredConfig();
                }

                var encrypted = File.ReadAllBytes(_configFilePath);
                var json = Decrypt(encrypted);
                var config = JsonSerializer.Deserialize<StoredConfig>(json) ?? new StoredConfig();

                var dirty = false;
                foreach (var instance in config.Instances)
                {
                    if (string.IsNullOrEmpty(instance.Id))
                    {
                        instance.Id = Guid.NewGuid().ToString("N");
                        dirty = true;
                    }
                }
                if (dirty)
                {
                    SaveInternal(config);
                }

                return config;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load configuration, starting empty");
                return new StoredConfig();
            }
        }

        private void SaveInternal(StoredConfig config)
        {
            var json = JsonSerializer.Serialize(config);
            var encrypted = Encrypt(json);
            File.WriteAllBytes(_configFilePath, encrypted);
        }

        private byte[] GetOrCreateKey()
        {
            if (File.Exists(_keyFilePath))
            {
                return Convert.FromBase64String(File.ReadAllText(_keyFilePath));
            }

            var key = new byte[32];
            RandomNumberGenerator.Fill(key);
            File.WriteAllText(_keyFilePath, Convert.ToBase64String(key));
            return key;
        }

        private byte[] Encrypt(string plainText)
        {
            using var aes = Aes.Create();
            aes.Key = GetOrCreateKey();
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream();
            ms.Write(aes.IV, 0, aes.IV.Length);
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            using (var sw = new StreamWriter(cs))
            {
                sw.Write(plainText);
            }
            return ms.ToArray();
        }

        private string Decrypt(byte[] cipherWithIv)
        {
            using var aes = Aes.Create();
            aes.Key = GetOrCreateKey();

            var iv = cipherWithIv.Take(16).ToArray();
            var cipherText = cipherWithIv.Skip(16).ToArray();
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
            using var ms = new MemoryStream(cipherText);
            using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
            using var sr = new StreamReader(cs);
            return sr.ReadToEnd();
        }
    }
}
