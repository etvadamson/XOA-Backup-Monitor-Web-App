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
                Name = i.Name,
                Url = i.Url,
                IsEnabled = i.IsEnabled,
                HasToken = !string.IsNullOrEmpty(i.ApiToken)
            }).ToList();
        }

        public async Task UpsertInstanceAsync(XOAInstance instance)
        {
            await _lock.WaitAsync();
            try
            {
                var config = LoadInternal();
                var existing = config.Instances.FirstOrDefault(i => i.Name == instance.Name);

                if (existing != null)
                {
                    existing.Url = instance.Url;
                    existing.IsEnabled = instance.IsEnabled;
                    if (!string.IsNullOrEmpty(instance.ApiToken))
                    {
                        existing.ApiToken = instance.ApiToken;
                    }
                }
                else
                {
                    config.Instances.Add(instance);
                }

                SaveInternal(config);
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
            await _lock.WaitAsync();
            try
            {
                if (!File.Exists(_settingsFilePath))
                {
                    return 30;
                }

                var json = await File.ReadAllTextAsync(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                return settings != null && settings.TryGetValue("RefreshInterval", out var v) ? v : 30;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read settings, defaulting to 30 minutes");
                return 30;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SetGlobalRefreshIntervalAsync(int minutes)
        {
            await _lock.WaitAsync();
            try
            {
                var settings = new Dictionary<string, int> { { "RefreshInterval", minutes } };
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_settingsFilePath, json);
            }
            finally
            {
                _lock.Release();
            }
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
                return JsonSerializer.Deserialize<StoredConfig>(json) ?? new StoredConfig();
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
