namespace Chaos.Tests;

public class ServerEndpointsTests
{
    [Fact]
    public void AccountPageIsHttps() =>
        Assert.Equal("https://chaos-rebirth.com/account/", ServerEndpoints.AccountPage.AbsoluteUri);
}
