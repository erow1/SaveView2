using FluentAssertions;
using SafeView.Application.Abstractions.Security;

namespace SafeView.Application.Tests.Security;

public class SecretsPolicyTests
{
    [Theory]
    [InlineData("password", true)]
    [InlineData("Password", true)]  // case-insensitive
    [InlineData("PASSWORD", true)]
    [InlineData("secret", true)]
    [InlineData("token", true)]
    [InlineData("api_key", true)]
    [InlineData("authToken", true)]
    [InlineData("accountSid", true)]
    [InlineData("to", false)]
    [InlineData("from", false)]
    [InlineData("host", false)]
    [InlineData("port", false)]
    [InlineData("subject", false)]
    [InlineData("", false)]
    public void IsSensitive_IdentifiesCorrectKeys(string key, bool expected)
        => SecretsPolicy.IsSensitive(key).Should().Be(expected);

    [Fact]
    public void SensitiveConfigKeys_ContainsAllExpected()
    {
        SecretsPolicy.SensitiveConfigKeys.Should().Contain("password");
        SecretsPolicy.SensitiveConfigKeys.Should().Contain("secret");
        SecretsPolicy.SensitiveConfigKeys.Should().Contain("token");
        SecretsPolicy.SensitiveConfigKeys.Should().Contain("api_key");
    }
}
