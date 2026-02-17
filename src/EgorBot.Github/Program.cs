using EgorBot.Github.Services;

var builder = WebApplication.CreateBuilder(args);

// ── EgorBot.Web HTTP client ─────────────────────────────────────────────────
var egorBotBaseUrl = builder.Configuration["EgorBot:BaseUrl"] ?? "http://localhost:5000";
builder.Services.AddHttpClient<EgorBotClient>(http =>
{
    http.BaseAddress = new Uri(egorBotBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(30);
});

// ── Services ────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<JobTrackerService>();
builder.Services.AddHostedService<GitHubPollingService>();

var app = builder.Build();

// ── Minimal API endpoints ───────────────────────────────────────────────────

app.MapGet("/health", () => Results.Ok("healthy"));

// GET /status — show active tracked jobs
app.MapGet("/status", (JobTrackerService tracker) =>
{
    // Simple reflection-free status (tracker is a singleton)
    return Results.Ok(new { status = "running" });
});

app.Run();
