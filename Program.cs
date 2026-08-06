using System.Text.Json.Serialization;
using XOABackupMonitorWeb.Models;
using XOABackupMonitorWeb.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("xoa");
builder.Services.AddSingleton<XoaApiService>();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<MonitorEngine>();
builder.Services.AddHostedService<RefreshBackgroundService>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

async Task<Dictionary<string, string>> BuildInstanceUrlMapAsync(ConfigService configService)
{
    var instances = await configService.LoadInstancesAsync();
    return instances.ToDictionary(i => i.Name, i => i.Url);
}

app.MapGet("/api/status", async (MonitorEngine engine, ConfigService configService) =>
{
    var instanceUrls = await BuildInstanceUrlMapAsync(configService);
    return Results.Ok(engine.GetStatusSnapshot(instanceUrls));
});

app.MapPost("/api/refresh", async (MonitorEngine engine, ConfigService configService) =>
{
    await engine.RefreshAllAsync(CancellationToken.None);
    var instanceUrls = await BuildInstanceUrlMapAsync(configService);
    return Results.Ok(engine.GetStatusSnapshot(instanceUrls));
});

app.MapPost("/api/refresh/{instanceName}", async (string instanceName, MonitorEngine engine, ConfigService configService) =>
{
    await engine.RefreshInstanceAsync(instanceName, CancellationToken.None);
    var instanceUrls = await BuildInstanceUrlMapAsync(configService);
    return Results.Ok(engine.GetStatusSnapshot(instanceUrls));
});

app.MapGet("/api/export/csv", (MonitorEngine engine) =>
{
    var csv = engine.ExportToCsv();
    var bytes = System.Text.Encoding.UTF8.GetBytes(csv);
    var fileName = $"XOA_Backup_Report_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
    return Results.File(bytes, "text/csv", fileName);
});

app.MapGet("/api/instances", async (ConfigService configService) =>
    Results.Ok(await configService.LoadInstanceSummariesAsync()));

app.MapPost("/api/instances", async (XOAInstance instance, ConfigService configService) =>
{
    if (string.IsNullOrWhiteSpace(instance.Name) || string.IsNullOrWhiteSpace(instance.Url))
    {
        return Results.BadRequest(new { error = "Name and Url are required." });
    }

    await configService.UpsertInstanceAsync(instance);
    return Results.Ok(await configService.LoadInstanceSummariesAsync());
});

// Updates an existing instance by its stable Id. Used by the "click a row to edit,
// then Save Instance" flow so edits target the right record instead of matching/creating
// by Name (which broke if the user also changed the Name field).
app.MapPut("/api/instances/{id}", async (string id, XOAInstance instance, ConfigService configService) =>
{
    if (string.IsNullOrWhiteSpace(instance.Name) || string.IsNullOrWhiteSpace(instance.Url))
    {
        return Results.BadRequest(new { error = "Name and Url are required." });
    }

    var updated = await configService.UpdateInstanceByIdAsync(id, instance);
    if (!updated)
    {
        return Results.NotFound(new { error = "Instance not found." });
    }

    return Results.Ok(await configService.LoadInstanceSummariesAsync());
});

// Flips Enabled for an existing instance without requiring the full Add/Update form.
// If the instance is being disabled, immediately strip it from the live dashboard state
// so it's excluded from the next background refresh cycle right away.
app.MapPost("/api/instances/{id}/toggle-enabled", async (string id, ConfigService configService, MonitorEngine engine) =>
{
    var newState = await configService.ToggleInstanceEnabledAsync(id);
    if (newState == null)
    {
        return Results.NotFound(new { error = "Instance not found." });
    }

    if (newState == false)
    {
        var instances = await configService.LoadInstancesAsync();
        var inst = instances.FirstOrDefault(i => i.Id == id);
        if (inst != null)
        {
            await engine.RemoveInstanceAsync(inst.Name);
        }
    }

    return Results.Ok(await configService.LoadInstanceSummariesAsync());
});

app.MapDelete("/api/instances/{name}", async (string name, ConfigService configService, MonitorEngine engine) =>
{
    await configService.DeleteInstanceAsync(name);
    await engine.RemoveInstanceAsync(name);
    return Results.Ok(await configService.LoadInstanceSummariesAsync());
});

app.MapPost("/api/instances/{name}/test", async (string name, ConfigService configService, XoaApiService apiService, CancellationToken ct) =>
{
    var instances = await configService.LoadInstancesAsync();
    var instance = instances.FirstOrDefault(i => i.Name == name);
    if (instance == null)
    {
        return Results.NotFound(new { error = "Instance not found." });
    }

    var ok = await apiService.TestConnectionAsync(instance.Url, instance.ApiToken, ct);
    return Results.Ok(new { success = ok });
});

app.MapPost("/api/test-connection", async (TestConnectionRequest req, XoaApiService apiService) =>
{
    if (string.IsNullOrWhiteSpace(req.Url) || string.IsNullOrWhiteSpace(req.ApiToken))
    {
        return Results.BadRequest(new { error = "Url and ApiToken are required." });
    }

    var ok = await apiService.TestConnectionAsync(req.Url, req.ApiToken, CancellationToken.None);
    return Results.Ok(new { success = ok });
});

app.MapGet("/api/history", async (string instance, string vm, ConfigService configService, XoaApiService apiService, CancellationToken ct) =>
{
    var instances = await configService.LoadInstancesAsync();
    var inst = instances.FirstOrDefault(i => i.Name == instance);
    if (inst == null)
    {
        return Results.NotFound(new { error = "Instance not found." });
    }

    if (string.IsNullOrEmpty(inst.ApiToken))
    {
        return Results.BadRequest(new { error = "No API token configured for this instance." });
    }

    var history = await apiService.GetVmHistoryAsync(inst.Url, inst.ApiToken, vm, ct: ct);
    return Results.Ok(history);
});

app.MapGet("/api/settings", async (ConfigService configService) =>
    Results.Ok(new
    {
        refreshIntervalMinutes = await configService.GetGlobalRefreshIntervalAsync(),
        maxConcurrentRequests = await configService.GetMaxConcurrentRequestsAsync()
    }));

app.MapPost("/api/settings", async (SettingsRequest req, ConfigService configService) =>
{
    if (req.RefreshIntervalMinutes < 1)
    {
        return Results.BadRequest(new { error = "refreshIntervalMinutes must be at least 1." });
    }

    if (req.MaxConcurrentRequests < 1)
    {
        return Results.BadRequest(new { error = "maxConcurrentRequests must be at least 1." });
    }

    await configService.SetGlobalRefreshIntervalAsync(req.RefreshIntervalMinutes);
    await configService.SetMaxConcurrentRequestsAsync(req.MaxConcurrentRequests);
    return Results.Ok(new
    {
        refreshIntervalMinutes = req.RefreshIntervalMinutes,
        maxConcurrentRequests = req.MaxConcurrentRequests
    });
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

app.Run();

record SettingsRequest(int RefreshIntervalMinutes, int MaxConcurrentRequests);
record TestConnectionRequest(string Url, string ApiToken);
