namespace Chaos;

/// <summary>
/// Where the launcher sends a player: the game's gate, which the patched client connects to, and
/// the server's web API.
/// </summary>
/// <remarks>
/// The gate is the only server address the client holds. It learns the account, lobby and duel
/// servers from the gate's directory when it connects, so pointing it at the gate is all a
/// client needs.
/// </remarks>
public sealed record ServerEndpoints(string GateHost, int GatePort, Uri Api)
{
    /// <summary>
    /// The Battle City server. The game's own name is not proxied, since a web proxy cannot carry
    /// the game's ports; the web API is reached through Cloudflare instead.
    /// </summary>
    public static ServerEndpoints Public { get; } =
        new("battle-city.net", 5730, new Uri("https://api.battle-city.net/"));

    public bool IsPublic => Equals(Public);

    /// <summary>Where a player makes an account: on the website, with Discord.</summary>
    public static Uri CreateAccountPage { get; } = new("https://chaos-rebirth.com/account/register/");

    /// <summary>Where a player sets a new password for an account.</summary>
    public static Uri ResetPasswordPage { get; } = new("https://chaos-rebirth.com/account/reset/");
}
