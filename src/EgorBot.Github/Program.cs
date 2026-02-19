using EgorBot.Github.Services;
using ModelContextProtocol.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// ── Load appsettings.Local.json (gitignored, holds secrets) ─────────────────
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ── EgorBot.Server HTTP client ─────────────────────────────────────────────────
var egorBotBaseUrl = builder.Configuration["EgorBot:BaseUrl"] ?? "http://localhost:5000";
builder.Services.AddHttpClient<EgorBotClient>(http =>
{
    http.BaseAddress = new Uri(egorBotBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(30);
});

// ── Services ────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<JobTrackerService>();
builder.Services.AddHostedService<GitHubPollingService>();

// ── MCP Server (Streamable HTTP transport) ──────────────────────────────────
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// ── Minimal API endpoints ───────────────────────────────────────────────────

app.MapGet("/health", () => Results.Ok("healthy"));

// ── MCP endpoint (Streamable HTTP at /mcp) ──────────────────────────────────
app.MapMcp();

// GET /status — show active tracked jobs
app.MapGet("/status", (JobTrackerService tracker) =>
{
    // Simple reflection-free status (tracker is a singleton)
    return Results.Ok(new { status = "running" });
});

app.Run();
