namespace Chaos.Tests;

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

sealed class RetailClientFactAttribute : FactAttribute
{
    public RetailClientFactAttribute()
    {
        if (RetailClient.Path is null)
            Skip = "set CHAOS_LAUNCHER_CLIENT to the February 2006 online_pc.dat";
    }
}
