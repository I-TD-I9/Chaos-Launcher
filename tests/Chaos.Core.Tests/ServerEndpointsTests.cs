namespace Chaos.Tests;

public class ServerEndpointsTests
{
    [Fact]
    public void Accounts_are_made_and_passwords_set_on_the_website_over_https()
    {
        Assert.Equal("https://chaos-rebirth.com/account/register/", ServerEndpoints.CreateAccountPage.AbsoluteUri);
        Assert.Equal("https://chaos-rebirth.com/account/reset/", ServerEndpoints.ResetPasswordPage.AbsoluteUri);
    }
}
