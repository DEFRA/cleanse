using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cleanse.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cleanse.Api.Test.Support;

/// <summary>
/// Hosts the real application end to end, on in-memory storage. Nothing is substituted except
/// the storage provider, so routing, authentication, serialisation and error mapping are all
/// exercised exactly as they run in production.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-api-key";
    public const string ResetKey = "test-reset-key";
    public const string ServiceVersion = "9.9.9-test";
    public const string CdpEnvironment = "test-host";

    /// <summary>
    /// Configuration arrives as environment variables rather than through ConfigureAppConfiguration.
    /// The composition root reads configuration eagerly to choose a storage provider, and that
    /// happens before WebApplicationFactory gets a chance to inject anything.
    /// </summary>
    static ApiFactory()
    {
        var settings = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = Environments.Development,
            ["Storage__Provider"] = "InMemory",
            ["Auth__ApiKey__Key"] = ApiKey,
            ["Auth__ApiKey__ClientName"] = "test-client",
            ["Auth__Jwt__Enabled"] = "false",
            ["Auth__Reset__Key"] = ResetKey,
            ["Api__MaxDetectionsPerRequest"] = "1000",
            // Pinned rather than inherited: the Development settings switch the engine on for
            // local use, and a test host must never start background workers because of that.
            ["Rules__Enabled"] = "false",
            // Likewise pinned away from the developer's real snapshot, so the diagnostics probe
            // reports the same thing on every machine and in CI.
            ["Rules__Krds__Provider"] = "LocalFile",
            ["Rules__Krds__LocalFilePath"] = Path.Combine(Path.GetTempPath(), "cleanse-no-such-snapshot.sqlite"),
            // What CDP injects into every container, so the diagnostics can say which build answered.
            ["SERVICE_VERSION"] = ServiceVersion,
            ["ENVIRONMENT"] = CdpEnvironment
        };

        foreach (var (key, value) in settings)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, ApiKey);
        return client;
    }

    /// <summary>An authenticated client that also carries the second secret the reset demands.</summary>
    public HttpClient CreateResetClient(string resetKey = ResetKey)
    {
        var client = CreateAuthenticatedClient();
        client.DefaultRequestHeaders.Add(ResetKeyOptions.HeaderName, resetKey);
        return client;
    }

    public HttpClient CreateClientWithKey(string key)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyOptions.HeaderName, key);
        return client;
    }

    public HttpClient CreateBearerClient(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

internal static class HttpExtensions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    public static Task<HttpResponseMessage> PutJsonAsync<T>(
        this HttpClient client, string url, T value) =>
        client.PutAsJsonAsync(url, value, Json);

    public static Task<HttpResponseMessage> PostJsonAsync<T>(
        this HttpClient client, string url, T value) =>
        client.PostAsJsonAsync(url, value, Json);
}
