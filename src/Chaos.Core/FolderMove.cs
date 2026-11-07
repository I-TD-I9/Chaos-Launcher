namespace Chaos;

/// <summary>Moving a folder the launcher kept under an old name to its new one.</summary>
public static class FolderMove
{
    /// <summary>
    /// The folder to use in place of <paramref name="oldFolder"/> from now on, which is
    /// <paramref name="newFolder"/>. The old one is renamed to it when only the old one exists,
    /// so nothing is copied; when both exist, or neither, nothing is touched. When the rename fails,
    /// most often because a file in the folder is open, the old folder is returned and stays as it
    /// was. Nothing is deleted or overwritten.
    /// </summary>
    public static string Adopt(string oldFolder, string newFolder)
    {
        if (Directory.Exists(newFolder) || !Directory.Exists(oldFolder))
            return newFolder;
        try
        {
            Directory.Move(oldFolder, newFolder);
            return newFolder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return oldFolder;
        }
    }
}
