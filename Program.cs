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

app.MapGet("/api/status", (MonitorEngine engine) => Results.Ok(engine.GetStatusSnapshot()));

app.MapPost("/api/refresh", async (MonitorEngine engine) =>
{
    await engine.RefreshAllAsync(CancellationToken.None);
    return Results.Ok(engine.GetStatusSnapshot());
});

app.MapPost("/api/refresh/{instanceName}", async (string instanceName, MonitorEngine engine) =>
{
    await engine.RefreshInstanceAsync(instanceName, CancellationToken.None);
    return Results.Ok(engine.GetStatusSnapshot());
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

    var history = await apiService.GetVmHistoryAsync(inst.Url, inst.ApiToken, vm, ct);
    return Results.Ok(history);
});

app.MapGet("/api/settings", async (ConfigService configService) =>
    Results.Ok(new { refreshIntervalMinutes = await configService.GetGlobalRefreshIntervalAsync() }));

app.MapPost("/api/settings", async (SettingsRequest req, ConfigService configService) =>
{
    if (req.RefreshIntervalMinutes < 1)
    {
        return Results.BadRequest(new { error = "refreshIntervalMinutes must be at least 1." });
    }

    await configService.SetGlobalRefreshIntervalAsync(req.RefreshIntervalMinutes);
    return Results.Ok(new { refreshIntervalMinutes = req.RefreshIntervalMinutes });
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

app.Run();

record SettingsRequest(int RefreshIntervalMinutes);
record TestConnectionRequest(string Url, string ApiToken);
