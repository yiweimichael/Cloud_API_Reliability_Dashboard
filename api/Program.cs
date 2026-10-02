using CloudApiReliabilityDashboard.Api.Data;
using Microsoft.EntityFrameworkCore;
using Endpoint = CloudApiReliabilityDashboard.Api.Data.Endpoint;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
}

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connectionString));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/endpoints", async (CreateEndpointRequest request, AppDbContext db) =>
{
    if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        return Results.BadRequest(new { error = "url must be an absolute http or https URL." });
    }

    var endpoint = new Endpoint { Url = uri.AbsoluteUri };
    db.Endpoints.Add(endpoint);
    await db.SaveChangesAsync();

    return Results.Created($"/endpoints/{endpoint.Id}", new { id = endpoint.Id });
});

app.MapGet("/endpoints", async (AppDbContext db) =>
    Results.Ok(await db.Endpoints
        .AsNoTracking()
        .OrderBy(e => e.Id)
        .Select(e => new { id = e.Id, url = e.Url })
        .ToListAsync()));

app.Run();

record CreateEndpointRequest(string? Url);
