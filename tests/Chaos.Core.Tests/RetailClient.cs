namespace Chaos.Tests;

/// <summary>
/// The retail online_pc.dat of February 2006, which the repository cannot hold: the file
/// CHAOS_LAUNCHER_CLIENT names, or the client of a game folder the launcher would find itself.
/// </summary>
static class RetailClient
{
    public static string? Path { get; } = Locate();

    static string? Locate()
    {
        var named = Environment.GetEnvironmentVariable("CHAOS_LAUNCHER_CLIENT");
        if (!string.IsNullOrEmpty(named))
            return named;
        var found = Game.GameLocator.Find(null);
        return found?.Status == Game.GameStatus.Ready ? found.Client : null;
    }
}

/// <summary>A test that needs the retail client, skipped where it is not on the machine.</summary>
sealed class RetailClientFactAttribute : FactAttribute
{
    public RetailClientFactAttribute()
    {
        if (RetailClient.Path is null)
            Skip = "needs the February 2006 client's online_pc.dat: set CHAOS_LAUNCHER_CLIENT to it";
    }
}
