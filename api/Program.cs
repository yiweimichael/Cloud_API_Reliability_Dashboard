using CloudApiReliabilityDashboard.Api.Checks;
using CloudApiReliabilityDashboard.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Endpoint = CloudApiReliabilityDashboard.Api.Data.Endpoint;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
builder.Services.Configure<CheckerOptions>(builder.Configuration.GetSection("Checker"));
builder.Services.AddScoped<CheckResultService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
builder.Services.AddAuthorization();
builder.Services.AddHttpClient("checker");
if (builder.Configuration.GetValue<bool>("Checker:Enabled", true))
{
    builder.Services.AddHostedService<EndpointCheckWorker>();
}

var app = builder.Build();

// Serve the React build (copied into wwwroot at publish time) ahead of auth so the page loads anonymously.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/endpoints", async (CreateEndpointRequest request, AppDbContext db) =>
{
    if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        return Results.BadRequest(new { error = "url must be an absolute http or https URL." });
    }

    // Compare normalized URLs so "https://example.com" and "https://example.com/" count as the same.
    // Checked in code rather than with a unique index: Url is nvarchar(max), and production already
    // holds duplicates that an index migration would trip over.
    if (await db.Endpoints.AnyAsync(e => e.Url == uri.AbsoluteUri))
    {
        return Results.Conflict(new { error = "This URL is already being monitored." });
    }

    var endpoint = new Endpoint { Url = uri.AbsoluteUri };
    db.Endpoints.Add(endpoint);
    await db.SaveChangesAsync();

    return Results.Created($"/endpoints/{endpoint.Id}", new { id = endpoint.Id });
}).RequireAuthorization();

app.MapGet("/endpoints", async (AppDbContext db) =>
    Results.Ok(await db.Endpoints
        .AsNoTracking()
        .OrderBy(e => e.Id)
        .Select(e => new { id = e.Id, url = e.Url })
        .ToListAsync()))
    .RequireAuthorization();

// Check history goes with it: the CheckResults foreign key cascades on delete.
app.MapDelete("/endpoints/{id:int}", async (int id, AppDbContext db) =>
    await db.Endpoints.Where(e => e.Id == id).ExecuteDeleteAsync() == 0
        ? Results.NotFound()
        : Results.NoContent())
    .RequireAuthorization();

app.MapGet("/endpoints/{id:int}/checks", async (int id, AppDbContext db) =>
{
    if (!await db.Endpoints.AnyAsync(e => e.Id == id))
    {
        return Results.NotFound();
    }

    var checks = await db.CheckResults
        .AsNoTracking()
        .Where(c => c.EndpointId == id)
        .OrderByDescending(c => c.CheckTimeUtc)
        .Take(50)
        .Select(c => new
        {
            id = c.Id,
            endpointId = c.EndpointId,
            checkTimeUtc = c.CheckTimeUtc,
            success = c.Success,
            latencyMs = c.LatencyMs,
            httpStatusCode = c.HttpStatusCode
        })
        .ToListAsync();

    return Results.Ok(checks);
}).RequireAuthorization();

app.Run();

record CreateEndpointRequest(string? Url);
