namespace Cleanse.Storage.Postgres.Test;

/// <summary>
/// Pure configuration checks. Deliberately outside the Postgres collection so they run without
/// Docker: these guard the rule that decides whether a password is required at all.
/// </summary>
public class CleansePostgresOptionsTests
{
    [Fact]
    public void A_password_is_required_when_this_config_builds_the_connection()
    {
        var options = new CleansePostgresOptions { UseIamAuthentication = false };

        var failure = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains(nameof(CleansePostgresOptions.LocalPassword), failure.Message);
    }

    [Fact]
    public void No_password_is_required_when_the_data_source_already_carries_the_credential()
    {
        // The placeholder these call sites used to pass read as a leaked credential to secret
        // scanners, which is why the case is modelled rather than faked.
        var options = CleansePostgresOptions.ForExistingDataSource();

        options.Validate();

        Assert.Null(options.LocalPassword);
        Assert.True(options.CredentialsSuppliedByDataSource);
    }

    [Fact]
    public void No_password_is_required_under_IAM_authentication()
    {
        new CleansePostgresOptions { UseIamAuthentication = true }.Validate();
    }

    [Fact]
    public void The_copy_threshold_is_carried_through_when_one_is_given()
    {
        Assert.Equal(3, CleansePostgresOptions.ForExistingDataSource(3).CopyThreshold);
        Assert.Equal(
            new CleansePostgresOptions().CopyThreshold,
            CleansePostgresOptions.ForExistingDataSource().CopyThreshold);
    }
}
