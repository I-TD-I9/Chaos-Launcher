namespace Chaos;

/// <summary>The server's addresses.</summary>
public sealed record ServerEndpoints(string GateHost, int GatePort, Uri Api)
{
    public static ServerEndpoints Public { get; } =
        new("battle-city.net", 5730, new Uri("https://api.battle-city.net/"));

    public bool IsPublic => Equals(Public);

    public static Uri AccountPage { get; } = new("https://chaos-rebirth.com/account/");
}
