using System.Text.Json;
using System.Text.RegularExpressions;
using Chaos.Client;

namespace Chaos.Pictures;

/// <summary>A card picture file from the server's manifest.</summary>
public readonly record struct ArtFile(string Path, string Sha256, long Size)
{
    public string StoredName => Sha256 + System.IO.Path.GetExtension(Path);
}

public sealed partial class ArtManifest
{
    public const int MaxFiles = 6000;
    public const long MaxFileSize = 2 * 1024 * 1024;
    public const long MaxTotalSize = 300L * 1024 * 1024;

    ArtManifest(IReadOnlyList<ArtFile> files) => Files = files;

    public IReadOnlyList<ArtFile> Files { get; }

    public static (ArtManifest? Manifest, string? Problem) Read(ReadOnlyMemory<byte> json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return (null, "not JSON");
        }
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number
            || !format.TryGetInt32(out int version) || version != 1
            || !root.TryGetProperty("files", out var list) || list.ValueKind != JsonValueKind.Array)
            return (null, "not a format 1 manifest");
        if (root.TryGetProperty("client", out var client)
            && (client.ValueKind != JsonValueKind.String || client.GetString() != SupportedClient.Version))
            return (null, $"pictures for client {client}, not {SupportedClient.Version}");
        if (list.GetArrayLength() > MaxFiles)
            return (null, $"too many files: {list.GetArrayLength()}");

        var files = new List<ArtFile>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in list.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("sha256", out var sha) || sha.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
                || !size.TryGetInt64(out long bytes))
                return (null, $"bad entry: {entry}");
            var file = new ArtFile(path.GetString()!, sha.GetString()!, bytes);
            if (!PicturePath().IsMatch(file.Path))
                return (null, $"bad path: {file.Path}");
            if (!Hash().IsMatch(file.Sha256))
                return (null, $"bad SHA-256 for {file.Path}: {file.Sha256}");
            if (file.Size is < 0 or > MaxFileSize)
                return (null, $"bad size for {file.Path}: {file.Size}");
            if (!paths.Add(file.Path))
                return (null, $"{file.Path} listed twice");
            total += file.Size;
            files.Add(file);
        }
        if (total > MaxTotalSize)
            return (null, $"too many bytes: {total}");
        return (new ArtManifest(files), null);
    }

    [GeneratedRegex(@"\Adata/(card|mini)/(?!(con|prn|aux|nul|com[0-9]|lpt[0-9])\.)[a-z0-9_]+\.(jpg|txt)\z", RegexOptions.CultureInvariant)]
    private static partial Regex PicturePath();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Hash();
}
