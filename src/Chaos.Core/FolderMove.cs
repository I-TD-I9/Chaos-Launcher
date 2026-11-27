namespace Chaos;

/// <summary>Moves the launcher's folder to its new name.</summary>
public static class FolderMove
{
    public static string Migrate(string oldFolder, string newFolder)
    {
        if (!Directory.Exists(oldFolder))
            return newFolder;
        if (Directory.Exists(newFolder) && !Directory.EnumerateFileSystemEntries(newFolder).Any())
        {
            try
            {
                Directory.Delete(newFolder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        if (Directory.Exists(newFolder))
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
