using Cleanse.Rules.Krds;

namespace Cleanse.Rules.Test;

public class KrdsOptionsTests
{
    [Fact]
    public void Accepts_a_complete_local_file_configuration()
    {
        var options = new KrdsOptions
        {
            Provider = nameof(KrdsSnapshotProvider.LocalFile),
            LocalFilePath = @"C:\snapshots\krds.sqlite"
        };

        Assert.Null(options.Describe());
    }

    [Fact]
    public void Accepts_a_complete_data_bridge_configuration()
    {
        var options = new KrdsOptions
        {
            Provider = nameof(KrdsSnapshotProvider.DataBridge),
            BaseUrl = "https://ls-keeper-data-bridge-backend.dev.cdp-int.defra.cloud",
            ApiKey = "a-key"
        };

        Assert.Null(options.Describe());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Mongo")]
    public void Rejects_an_unknown_provider(string provider)
    {
        var problem = new KrdsOptions { Provider = provider }.Describe();

        Assert.Contains("Provider", problem);
    }

    [Fact]
    public void Requires_a_path_for_the_local_file_provider()
    {
        var options = new KrdsOptions { Provider = nameof(KrdsSnapshotProvider.LocalFile) };

        Assert.Contains("LocalFilePath", options.Describe());
    }

    [Theory]
    [InlineData(null, "a-key", "BaseUrl")]
    [InlineData("not-a-url", "a-key", "absolute")]
    [InlineData("https://example.test", null, "ApiKey")]
    public void Reports_what_the_data_bridge_provider_is_missing(string? baseUrl, string? apiKey, string expected)
    {
        var options = new KrdsOptions
        {
            Provider = nameof(KrdsSnapshotProvider.DataBridge),
            BaseUrl = baseUrl,
            ApiKey = apiKey
        };

        Assert.Contains(expected, options.Describe());
    }
}
