using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, BenchmarkJsonContext.Default));

var app = builder.Build();

app.MapGet("/health", static () => TypedResults.NoContent());

app.MapPost(
    "/api/customers/{customerId:int}/quotes",
    static (
        [FromRoute] int customerId,
        [FromQuery] float taxRate,
        [FromQuery] DateTimeOffset asOf,
        [FromQuery] string currency,
        [FromQuery] string campaign,
        [FromHeader(Name = "X-Tenant")] string tenant,
        QuoteRequest request) =>
    {
        var subtotal = 0.0f;
        var totalWeight = 0.0f;
        var itemCount = 0;
        var freshnessDiscount = 0.0f;

        foreach (var item in request.Items)
        {
            var lineTotal = item.UnitPrice * item.Quantity;
            subtotal += lineTotal;
            totalWeight += item.WeightKg * item.Quantity;
            itemCount += item.Quantity;

            var daysAvailable = Math.Clamp(
                (float)(asOf - item.AvailableSince).TotalDays,
                0.0f,
                365.0f);
            freshnessDiscount += lineTotal * (1.0f - daysAvailable / 365.0f) * 0.015f;
        }

        var regionFactor = request.Customer.Region switch
        {
            "EU" => 1.08f,
            "APAC" => 1.18f,
            _ => 1.0f,
        };
        var priorityFactor = request.Shipping.Priority ? 1.35f : 1.0f;
        var shipping = (4.25f + totalWeight * 0.72f + request.Shipping.DistanceKm * 0.0035f)
            * regionFactor
            * priorityFactor;

        var loyaltyDiscount = subtotal * Math.Clamp(request.Customer.LoyaltyScore, 0.0f, 1.0f) * 0.08f;
        var campaignDiscount = campaign.StartsWith("fall-", StringComparison.OrdinalIgnoreCase)
            ? subtotal * 0.035f
            : 0.0f;
        var discount = MathF.Min(subtotal * 0.30f, loyaltyDiscount + campaignDiscount + freshnessDiscount);
        var taxable = subtotal - discount + shipping;
        var tax = taxable * Math.Clamp(taxRate, 0.0f, 0.25f);
        var total = MathF.Round(taxable + tax, 2);

        var response = new QuoteResponse(
            QuoteId: string.Concat(
                tenant,
                "-",
                request.RequestId,
                "-",
                customerId.ToString(CultureInfo.InvariantCulture)),
            CustomerName: request.Customer.Name,
            Currency: currency.ToUpperInvariant(),
            ItemCount: itemCount,
            Subtotal: MathF.Round(subtotal, 2),
            Discount: MathF.Round(discount, 2),
            Shipping: MathF.Round(shipping, 2),
            Tax: MathF.Round(tax, 2),
            Total: total,
            GeneratedAt: asOf,
            ValidUntil: asOf.AddMinutes(15),
            DeliveryEstimate: asOf.AddDays(request.Shipping.Priority ? 2 : 5),
            ShippingZone: string.Concat(request.Customer.Region, "-", request.Shipping.PostalCode[..3]),
            Warnings: totalWeight > 25.0f ? ["heavy-shipment"] : []);

        return TypedResults.Ok(response);
    });

if (int.TryParse(
        Environment.GetEnvironmentVariable("BENCHMARK_EXIT_AFTER_SECONDS"),
        NumberStyles.None,
        CultureInfo.InvariantCulture,
        out var exitAfterSeconds)
    && exitAfterSeconds > 0)
{
    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(exitAfterSeconds));
    await app.RunAsync(shutdown.Token);
}
else
{
    await app.RunAsync();
}

public sealed record QuoteRequest(
    string RequestId,
    DateTimeOffset SubmittedAt,
    CustomerInput Customer,
    ShippingInput Shipping,
    LineItemInput[] Items);

public sealed record CustomerInput(
    string Name,
    string Region,
    string Segment,
    float LoyaltyScore);

public sealed record ShippingInput(
    string PostalCode,
    float DistanceKm,
    bool Priority);

public sealed record LineItemInput(
    string Sku,
    string Description,
    int Quantity,
    float UnitPrice,
    float WeightKg,
    DateTimeOffset AvailableSince);

public sealed record QuoteResponse(
    string QuoteId,
    string CustomerName,
    string Currency,
    int ItemCount,
    float Subtotal,
    float Discount,
    float Shipping,
    float Tax,
    float Total,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ValidUntil,
    DateTimeOffset DeliveryEstimate,
    string ShippingZone,
    string[] Warnings);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(QuoteRequest))]
[JsonSerializable(typeof(QuoteResponse))]
internal sealed partial class BenchmarkJsonContext : JsonSerializerContext;
