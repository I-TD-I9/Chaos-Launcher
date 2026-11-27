using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Chaos.Titles;

/// <summary>Result of a title names update.</summary>
public enum TitlesOutcome
{
    Written,
    None,
    Unreachable,
    Refused,
    Unwritable,
}

/// <summary>A GET /api/titles reply.</summary>
public readonly record struct TitlesReply(TitlesOutcome Outcome, byte[]? File);

public sealed class TitleNames(HttpClient http, Uri api, TimeProvider? clock = null)
{
    public const string FileName = "chaos_titles.bin";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    internal const int First = 0x1000;
    internal const int Last = 0x1FFF;
    internal const int HeaderLength = 16;
    internal const int RecordLength = 64;
    internal const int MaxNameLength = 59;
    internal const uint DefaultColour = 0xFFFFFFFF;

    const int MaxReplySize = 1024 * 1024;

    readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<TitlesOutcome> UpdateAsync(string folder, CancellationToken cancel = default) =>
        Write(folder, await FetchAsync(cancel));

    public async Task<TitlesReply> FetchAsync(CancellationToken cancel = default)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, clock);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token);
        try
        {
            using var response = await http.GetAsync(new Uri(api, "api/titles"), HttpCompletionOption.ResponseHeadersRead, limit.Token);
            // 404: an older server without titles.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new TitlesReply(TitlesOutcome.None, null);
            if (response.StatusCode != HttpStatusCode.OK)
                return new TitlesReply(TitlesOutcome.Unreachable, null);
            // Read as bytes, ignoring the charset.
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, limit.Token)) > 0)
            {
                if (body.Length + read > MaxReplySize)
                    return new TitlesReply(TitlesOutcome.Refused, null);
                body.Write(buffer, 0, read);
            }
            return ParseReply(body.ToArray()) is { } file
                ? new TitlesReply(TitlesOutcome.Written, file)
                : new TitlesReply(TitlesOutcome.Refused, null);
        }
        catch (Exception e) when ((e is HttpRequestException or IOException or OperationCanceledException)
                                  && !cancel.IsCancellationRequested)
        {
            return new TitlesReply(TitlesOutcome.Unreachable, null);
        }
    }

    public static TitlesOutcome Write(string folder, TitlesReply reply)
    {
        string path = Path.Combine(folder, FileName);
        try
        {
            var bc = new DirectoryInfo(folder);
            if (bc.Exists && bc.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return TitlesOutcome.Unwritable;
            switch (reply.Outcome)
            {
                case TitlesOutcome.Written when reply.File is { } file:
                    Directory.CreateDirectory(folder);
                    LauncherFiles.Replace(path, file);
                    return TitlesOutcome.Written;
                case TitlesOutcome.None:
                    // Delete the link, not its target.
                    if (bc.Exists)
                        File.Delete(path);
                    return TitlesOutcome.None;
                default:
                    return reply.Outcome == TitlesOutcome.Written ? TitlesOutcome.Refused : reply.Outcome;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return TitlesOutcome.Unwritable;
        }
    }

    internal static byte[]? ParseReply(ReadOnlySpan<byte> reply)
    {
        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(reply.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array)
                return null;
            var own = new List<OwnTitle>();
            foreach (var title in titles.EnumerateArray())
            {
                if (title.ValueKind != JsonValueKind.Object || !title.TryGetProperty("number", out var number) ||
                    number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out int n) || n is < First or > Last)
                    continue;
                uint colour = title.TryGetProperty("colour", out var c) && c.ValueKind == JsonValueKind.Number &&
                              c.TryGetUInt32(out uint value) ? value : DefaultColour;
                // Hidden titles have no name without a session.
                string? name = title.TryGetProperty("name", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                own.Add(new OwnTitle(n, colour, name));
            }
            return Build(own);
        }
    }

    internal readonly record struct OwnTitle(int Number, uint Colour, string? Name);

    internal static byte[] Build(IReadOnlyCollection<OwnTitle> titles)
    {
        int count = titles.Count == 0 ? 0 : titles.Max(title => title.Number) - First + 1;
        var file = new byte[HeaderLength + count * RecordLength];
        "CHAOSTTL"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8), count);
        var given = new bool[count];
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(HeaderLength + i * RecordLength), DefaultColour);
        foreach (var title in titles)
        {
            int i = title.Number - First;
            if (given[i])
                continue;
            given[i] = true;
            var record = file.AsSpan(HeaderLength + i * RecordLength, RecordLength);
            BinaryPrimitives.WriteUInt32LittleEndian(record, title.Colour);
            if (Fits(title.Name))
                Encoding.ASCII.GetBytes(title.Name, record[4..]);
        }
        return file;
    }

    internal static bool Fits([NotNullWhen(true)] string? name) =>
        name is { Length: > 0 and <= MaxNameLength } && name[0] != ' ' && name[^1] != ' '
        && name.All(c => c is >= ' ' and <= '~' and not ('<' or '>' or '&'));
}
