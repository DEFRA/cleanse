using System.Net;
using Cleanse.Api.Test.Support;
using Cleanse.Auth;

namespace Cleanse.Api.Test;

public class AuthenticationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task An_unauthenticated_request_is_rejected()
    {
        var response = await factory.CreateClient().GetAsync("/v1/issues");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_api_key_is_rejected()
    {
        var response = await factory.CreateClientWithKey("not-the-key").GetAsync("/v1/issues");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_key_that_is_a_prefix_of_the_real_one_is_rejected()
    {
        var response = await factory.CreateClientWithKey(ApiFactory.ApiKey[..5]).GetAsync("/v1/issues");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_correct_api_key_is_accepted()
    {
        var response = await factory.CreateAuthenticatedClient().GetAsync("/v1/issues");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_bearer_token_is_rejected_when_the_jwt_scheme_is_disabled()
    {
        // Mirrors local development, where LocalStack cannot issue a web identity token.
        var response = await factory.CreateBearerClient("not.a.token").GetAsync("/v1/issues");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void The_api_key_header_does_not_collide_with_the_gateway()
    {
        // x-api-key belongs to the CDP ephemeral API Gateway; ours must be distinct.
        Assert.NotEqual("x-api-key", ApiKeyOptions.HeaderName);
        Assert.Equal("x-cleanse-api-key", ApiKeyOptions.HeaderName);
    }

    [Fact]
    public async Task Health_is_anonymous()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_openapi_document_is_anonymous_and_describes_both_schemes()
    {
        var response = await factory.CreateClient().GetAsync("/v1/openapi.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var document = await response.ReadJsonAsync();
        var schemes = document.GetProperty("components").GetProperty("securitySchemes");

        Assert.True(schemes.TryGetProperty("ApiKey", out _));
        Assert.True(schemes.TryGetProperty("Bearer", out _));
        Assert.Equal(
            ApiKeyOptions.HeaderName,
            schemes.GetProperty("ApiKey").GetProperty("name").GetString());
    }
}
