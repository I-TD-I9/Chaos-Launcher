namespace Chaos.Game;

/// <summary>
/// The stone slab carved with hieroglyphs that the launcher's window is laid on: the backdrop of
/// the game's own deck dialog, read out of the player's game folder each time, so that nothing of
/// Konami's ships with the launcher or is written to disk.
/// </summary>
public static class SlabTexture
{
    /// <summary>The slab's entry in the game's archives: a 24-bit BMP of 504 x 280 in the February folder.</summary>
    public const string Entry = @"data\y\deck_c\kabegami\dialog_base01.bmp";

    /// <summary>
    /// The slab as the bytes of a BMP file, from the archives the game in
    /// <paramref name="gameFolder"/> reads; or null when there is no folder, no archive, no such
    /// entry, or anything about it fails. It never throws: the window goes without the slab.
    /// </summary>
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
            // Whatever it was, the window is whole without it.
            return null;
        }
    }

    /// <summary>The slab from the folder <paramref name="game"/> found, as <see cref="Read(string?)"/>.</summary>
    public static byte[]? Read(GameFolder? game) => Read(game?.Path);
}
