namespace XOABackupMonitorWeb.Services
{
    public class RefreshBackgroundService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<RefreshBackgroundService> _logger;

        public RefreshBackgroundService(IServiceScopeFactory scopeFactory, ILogger<RefreshBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using (var startupScope = _scopeFactory.CreateScope())
            {
                var engine = startupScope.ServiceProvider.GetRequiredService<MonitorEngine>();
                await engine.LoadFromCacheAsync();
            }

            await RunRefreshAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                int intervalMinutes;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var configService = scope.ServiceProvider.GetRequiredService<ConfigService>();
                    intervalMinutes = await configService.GetGlobalRefreshIntervalAsync();
                }

                if (intervalMinutes < 1) intervalMinutes = 30;

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                await RunRefreshAsync(stoppingToken);
            }
        }

        private async Task RunRefreshAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<MonitorEngine>();
                await engine.RefreshAllAsync(ct);
                _logger.LogInformation("Background refresh completed at {Time}", DateTime.Now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background refresh failed");
            }
        }
    }
}
