using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudApiReliabilityDashboard.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CloudApiReliabilityDashboard.Api.Tests;

public sealed class EndpointRouteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public EndpointRouteTests()
    {
        // The in-memory database lives only as long as this connection stays open.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // Program.cs refuses to start without a connection string; the value is never used.
            builder.UseSetting("ConnectionStrings:DefaultConnection", "unused");
            // Keep the background checker out of route tests so they never make outbound HTTP calls.
            builder.UseSetting("Checker:Enabled", "false");

            builder.ConfigureServices(services =>
            {
                // Remove the SQL Server registration, including the options configuration action
                // that AddDbContext registers (an internal EF type, matched by name), otherwise two
                // providers end up configured.
                var descriptors = services
                    .Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)
                        || (d.ServiceType.IsGenericType
                            && d.ServiceType.GetGenericTypeDefinition().Name == "IDbContextOptionsConfiguration`1"
                            && d.ServiceType.GenericTypeArguments[0] == typeof(AppDbContext)))
                    .ToList();
                foreach (var descriptor in descriptors)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

                // Swap Entra ID JWT validation for the fake scheme so tests don't need real tokens.
                services.AddAuthentication(options =>
                    {
                        options.DefaultScheme = TestAuthHandler.SchemeName;
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
            });
        });

        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        }

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestAuthHandler.SchemeName);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _connection.Dispose();
    }

    private async Task<int> CreateEndpointAsync(string url = "https://example.com/")
    {
        var response = await _client.PostAsJsonAsync("/endpoints", new { url });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task PostEndpoints_ReturnsCreatedWithId()
    {
        var response = await _client.PostAsJsonAsync("/endpoints", new { url = "https://example.com/" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetInt32();
        Assert.True(id > 0);
        Assert.Equal($"/endpoints/{id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task PostEndpoints_WithBadUrl_ReturnsBadRequest()
    {
        var response = await _client.PostAsJsonAsync("/endpoints", new { url = "not-a-url" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostEndpoints_WithExistingUrl_ReturnsConflict()
    {
        await CreateEndpointAsync("https://example.com/");

        // No trailing slash: normalizes to the same URL as the existing endpoint.
        var response = await _client.PostAsJsonAsync("/endpoints", new { url = "https://example.com" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var list = await _client.GetFromJsonAsync<JsonElement>("/endpoints");
        Assert.Single(list.EnumerateArray());
    }

    [Fact]
    public async Task DeleteEndpoint_RemovesEndpointAndItsChecks()
    {
        var id = await CreateEndpointAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await new CheckResultService(db).SaveCheckResultAsync(id, true, 123, 200);
        }

        var response = await _client.DeleteAsync($"/endpoints/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var list = await _client.GetFromJsonAsync<JsonElement>("/endpoints");
        Assert.Empty(list.EnumerateArray());
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.CheckResults.AnyAsync(c => c.EndpointId == id));
        }
    }

    [Fact]
    public async Task DeleteEndpoint_ForUnknownEndpoint_ReturnsNotFound()
    {
        var response = await _client.DeleteAsync("/endpoints/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetEndpoints_ListsCreatedEndpoint()
    {
        var id = await CreateEndpointAsync("https://example.com/health");

        var response = await _client.GetAsync("/endpoints");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = Assert.Single(list.EnumerateArray());
        Assert.Equal(id, item.GetProperty("id").GetInt32());
        Assert.Equal("https://example.com/health", item.GetProperty("url").GetString());
    }

    [Fact]
    public async Task GetChecks_ReturnsSavedSample()
    {
        var id = await CreateEndpointAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await new CheckResultService(db).SaveCheckResultAsync(id, true, 123, 200);
        }

        var response = await _client.GetAsync($"/endpoints/{id}/checks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<JsonElement>();
        var check = Assert.Single(list.EnumerateArray());
        Assert.Equal(id, check.GetProperty("endpointId").GetInt32());
        Assert.True(check.GetProperty("success").GetBoolean());
        Assert.Equal(123, check.GetProperty("latencyMs").GetInt32());
        Assert.Equal(200, check.GetProperty("httpStatusCode").GetInt32());
    }

    [Fact]
    public async Task GetChecks_ReturnsNewestFirst()
    {
        var id = await CreateEndpointAsync();
        var older = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var newer = older.AddMinutes(1);

        using (var scope = _factory.Services.CreateScope())
        {
            // Insert directly with fixed timestamps; the older row goes in first so insertion order
            // alone would return it first.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CheckResults.Add(new CheckResult { EndpointId = id, CheckTimeUtc = older, Success = true, LatencyMs = 100, HttpStatusCode = 200 });
            db.CheckResults.Add(new CheckResult { EndpointId = id, CheckTimeUtc = newer, Success = false, LatencyMs = 200, HttpStatusCode = 500 });
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/endpoints/{id}/checks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal(200, list[0].GetProperty("latencyMs").GetInt32());
        Assert.Equal(100, list[1].GetProperty("latencyMs").GetInt32());
    }

    [Fact]
    public async Task GetChecks_ForUnknownEndpoint_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/endpoints/999/checks");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetEndpoints_WithoutToken_ReturnsUnauthorized()
    {
        using var anonymousClient = _factory.CreateClient();

        var response = await anonymousClient.GetAsync("/endpoints");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
