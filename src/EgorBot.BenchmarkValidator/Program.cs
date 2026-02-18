using EgorBot.BenchmarkValidator.Models;
using EgorBot.BenchmarkValidator.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Load optional local config ──────────────────────────────────────────────
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// ── Services ────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<BenchmarkValidationService>();

var app = builder.Build();

// ── Startup diagnostics ─────────────────────────────────────────────────────
{
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var tfm = app.Configuration["Validator:TargetFramework"] ?? "net10.0";
    var maxBench = app.Configuration.GetValue("Validator:MaxBenchmarkCount", 40);
    log.LogInformation("Config: TFM={Tfm}, MaxBenchmarkCount={Max}", tfm, maxBench);
}

// ═════════════════════════════════════════════════════════════════════════════
//  Endpoints
// ═════════════════════════════════════════════════════════════════════════════

app.MapGet("/health", () => Results.Ok("healthy"));

// POST /api/validate — validate a benchmark snippet
app.MapPost("/api/validate", async (ValidateRequest request, BenchmarkValidationService validator, CancellationToken ct) =>
{
    // No benchmark code → skip validation (dotnet/performance — will be validated later)
    if (string.IsNullOrWhiteSpace(request.BenchmarkCode))
    {
        return Results.Ok(new ValidateResponse
        {
            IsValid = true,
            BenchmarkCount = 0,
        });
    }

    var (isValid, count, error) = await validator.ValidateAsync(request.BenchmarkCode, request.BdnArguments, ct);

    return Results.Ok(new ValidateResponse
    {
        IsValid = isValid,
        BenchmarkCount = count,
        Error = error,
    });
});

app.Run();
