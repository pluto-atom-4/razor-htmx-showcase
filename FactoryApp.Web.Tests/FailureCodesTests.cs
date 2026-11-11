using FactoryApp.Domain;

namespace FactoryApp.Web.Tests;

public class FailureCodesTests
{
    [Theory]
    [InlineData("LEAK")]
    [InlineData("OVERPRESSURE")]
    [InlineData("SENSOR_FAULT")]
    [InlineData("TIMEOUT")]
    [InlineData("OTHER")]
    public void Known_codes_are_valid(string code) => Assert.True(FailureCodes.IsValid(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("leak")]
    [InlineData("LEAK ")]
    [InlineData("UNKNOWN")]
    public void Missing_lowercase_padded_or_unknown_codes_are_invalid(string? code) =>
        Assert.False(FailureCodes.IsValid(code));
}
