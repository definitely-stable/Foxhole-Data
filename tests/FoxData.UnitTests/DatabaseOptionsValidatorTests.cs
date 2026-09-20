using FoxData.Infrastructure.Configuration;

namespace FoxData.UnitTests;

public sealed class DatabaseOptionsValidatorTests
{
    [Fact]
    public void EmptyConnectionStringIsRejected()
    {
        var validator = new DatabaseOptionsValidator();

        var result = validator.Validate(null, new DatabaseOptions());

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void SyntacticallyValidConnectionStringIsAccepted()
    {
        var validator = new DatabaseOptionsValidator();

        var result = validator.Validate(
            null,
            new DatabaseOptions
            {
                ConnectionString = "Host=localhost;Database=foxdata;Username=foxdata;Password=dev",
            });

        Assert.True(result.Succeeded);
    }
}
