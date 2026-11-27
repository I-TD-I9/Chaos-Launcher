namespace Chaos.Game;

/// <summary>The hieroglyph slab behind the window.</summary>
public static class SlabTexture
{
    public const string Entry = @"data\y\deck_c\kabegami\dialog_base01.bmp";

    public static byte[]? Read(string? gameFolder)
    {
        if (string.IsNullOrEmpty(gameFolder))
            return null;
        try
        {
            if (!Directory.Exists(gameFolder))
                return null;
            var bytes = GameArchive.Read(GameArchive.InFolder(gameFolder), Entry);
            return bytes is { Length: > 54 } && bytes[0] == 'B' && bytes[1] == 'M' ? bytes : null;
        }
        catch (Exception)
        {
            // The window works without it.
            return null;
        }
    }

    public static byte[]? Read(GameFolder? game) => Read(game?.Path);
}
