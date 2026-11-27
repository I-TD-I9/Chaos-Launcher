namespace Chaos;

static class LauncherFiles
{
    public static void Replace(string path, ReadOnlySpan<byte> bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            return;
        string partial = path + ".new";
        try
        {
            File.WriteAllBytes(partial, bytes);
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Overwritten by the next attempt.
        }
    }
}
