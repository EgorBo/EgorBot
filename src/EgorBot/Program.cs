using EgorBot.Api;
using EgorBot.Cloud;
using EgorBot.Cloud.Implementations;
using EgorBot.Data;
using EgorBot.Services;
using EgorBot.Services.GitHub;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──
builder.Services.AddDbContext<BotDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("BotDb") ?? "Data Source=egorbot.db"));

// ── Cloud providers ──
builder.Services.AddSingleton<ICloudProvider, LocalExecution>();
builder.Services.AddSingleton<ICloudProvider, AzureCloudProvider>();
builder.Services.AddSingleton<ICloudProvider, Ec2CloudProvider>();

// ── Services ──
builder.Services.AddSingleton<LogStore>();
builder.Services.AddScoped<GitHubService>();
builder.Services.AddScoped<ScriptGenerator>();
builder.Services.AddScoped<JobOrchestrator>();

// ── Background services ──
builder.Services.AddHostedService<GitHubMonitorService>();
builder.Services.AddHostedService<TimeoutWatchdogService>();

var app = builder.Build();

// ── Ensure database is created ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// ── Static files (wwwroot) ──
app.UseDefaultFiles();
app.UseStaticFiles();

// ── API endpoints ──
app.MapBotApi();

app.Run();
