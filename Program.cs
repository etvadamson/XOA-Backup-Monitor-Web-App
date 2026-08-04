using XOABackupMonitorWeb.Models;
using XOABackupMonitorWeb.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("xoa");
builder.Services.AddSingleton<XoaApiService>();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<CacheService>();
builder.Services.AddSingleton<MonitorEngine>();
builder.Services.AddHostedService<RefreshBackgroundService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", (MonitorEngine engine) => Results.Ok(engine.GetStatusSnapshot()));

// Refresh endpoints intentionally use CancellationToken.None instead of the request's
// token (HttpContext.RequestAborted). If we used the request token, a browser tab close,
// navigation, or double-click of "Refresh Now" would cancel the in-flight HTTP calls to
// the XOA host mid-fetch (visible as TaskCanceledException / SocketException(995) in logs),
// leaving that instance's status stuck on an error until the next background cycle.
// Using None guarantees the refresh always runs to completion and updates shared state,
// regardless of what the calling browser does.
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

app.MapDelete("/api/instances/{name}", async (string name, ConfigService configService) =>
{
    await configService.DeleteInstanceAsync(name);
    return Results.Ok(await configService.LoadInstanceSummariesAsync());
});

// Legacy endpoint: tests a SAVED instance by name. Kept for backward compatibility.
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

// Tests a URL/token pair directly, without requiring the instance to be saved first.
// This is what the "Test Connection" button in the Configure modal now uses.
app.MapPost("/api/test-connection", async (TestConnectionRequest req, XoaApiService apiService) =>
{
    if (string.IsNullOrWhiteSpace(req.Url) || string.IsNullOrWhiteSpace(req.ApiToken))
    {
        return Results.BadRequest(new { error = "Url and ApiToken are required." });
    }

    var ok = await apiService.TestConnectionAsync(req.Url, req.ApiToken, CancellationToken.None);
    return Results.Ok(new { success = ok });
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
