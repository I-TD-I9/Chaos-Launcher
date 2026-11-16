using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Chaos.Titles;

/// <summary>How bringing the titles' names up to date went.</summary>
public enum TitlesOutcome
{
    /// <summary>The file holds the server's titles.</summary>
    Written,

    /// <summary>The server has no titles to give, so there is no file.</summary>
    None,

    /// <summary>The server could not be reached in time, or was in no state to answer: the file is as it was.</summary>
    Unreachable,

    /// <summary>What the server gave was no list of titles: the file is as it was.</summary>
    Refused,

    /// <summary>The file could not be written or taken away: it is as it was.</summary>
    Unwritable,
}

/// <summary>What GET /api/titles gave: how it went and, when it gave a list, the file to write from it.</summary>
public readonly record struct TitlesReply(TitlesOutcome Outcome, byte[]? File);

/// <summary>
/// Writes the names and plate colours of the server's own titles, 0x1000 to 0x1fff, into bc for
/// the patched client to read (ClientPatcher.NameOwnTitles), from the server's GET /api/titles.
/// It runs before the game starts: the client reads the file once, at the first title it draws.
/// </summary>
/// <remarks>
/// The file is <c>"CHAOSTTL"</c>, a u32 count and a u32 0, then count records of 64 bytes, record
/// i being title 0x1000 + i: a u32 COLORREF, 0x00bbggrr, or 0xffffffff for the client's own
/// near-black, then the name in 60 bytes, zeros after it. A record runs for every number from
/// 0x1000 up to the highest the server gives, those it doesn't give left empty. The client puts a
/// title's name into its own markup as it is, and on a Japanese or Korean system pairs up bytes
/// past 0x7f, so a name that isn't plain printable ASCII, has &lt;, &gt; or &amp; in it, or is
/// longer than 59 bytes is left empty, which the client shows as "Unknown". When the server can't
/// be reached, or gives something that is no list, the file it gave last stays.
/// </remarks>
public sealed class TitleNames(HttpClient http, Uri api, TimeProvider? clock = null)
{
    public const string FileName = "chaos_titles.bin";

    /// <summary>How long the server is given to answer, the whole reply included, before the game starts without it.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    internal const int First = 0x1000;
    internal const int Last = 0x1FFF;
    internal const int HeaderLength = 16;
    internal const int RecordLength = 64;
    internal const int NameMost = 59;
    internal const uint DefaultColour = 0xFFFFFFFF;

    // Every title the server can have, and all of the game's own, come to well under this.
    const int LargestReply = 1024 * 1024;

    readonly TimeProvider clock = clock ?? TimeProvider.System;

    /// <summary>Asks the server for its titles and writes them into <paramref name="folder"/>, bc.</summary>
    public async Task<TitlesOutcome> UpdateAsync(string folder, CancellationToken cancel = default) =>
        Write(folder, await FetchAsync(cancel));

    /// <summary>The server's titles, as the file to write, within <see cref="Patience"/>.</summary>
    public async Task<TitlesReply> FetchAsync(CancellationToken cancel = default)
    {
        using var patience = new CancellationTokenSource(Patience, clock);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel, patience.Token);
        try
        {
            using var response = await http.GetAsync(new Uri(api, "api/titles"), HttpCompletionOption.ResponseHeadersRead, limit.Token);
            // Not found: a server from before the titles. Any other refusal is the server in
            // trouble, or Cloudflare in front of it.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new TitlesReply(TitlesOutcome.None, null);
            if (response.StatusCode != HttpStatusCode.OK)
                return new TitlesReply(TitlesOutcome.Unreachable, null);
            // As bytes, whatever charset the reply claims (ServerInfo.FetchAsync says why).
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            using var body = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, limit.Token)) > 0)
            {
                if (body.Length + read > LargestReply)
                    return new TitlesReply(TitlesOutcome.Refused, null);
                body.Write(buffer, 0, read);
            }
            return FileFrom(body.ToArray()) is { } file
                ? new TitlesReply(TitlesOutcome.Written, file)
                : new TitlesReply(TitlesOutcome.Refused, null);
        }
        catch (Exception e) when ((e is HttpRequestException or IOException or OperationCanceledException)
                                  && !cancel.IsCancellationRequested)
        {
            return new TitlesReply(TitlesOutcome.Unreachable, null);
        }
    }

    /// <summary>
    /// Writes the file <paramref name="reply"/> gave into <paramref name="folder"/>, or takes it
    /// away when the server has no titles; otherwise leaves the file there as it is. The bytes go
    /// to a file beside it first, so the client never finds half a file.
    /// </summary>
    public static TitlesOutcome Write(string folder, TitlesReply reply)
    {
        string path = Path.Combine(folder, FileName);
        try
        {
            // The launcher makes no links, and one in bc's place could lead the write out of it
            // (CardPictures takes such a link away when it next runs).
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
                    // A link goes, never what it leads to.
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

    /// <summary>
    /// The file for a GET /api/titles reply, <c>{"ok": true, "titles": [...]}</c>, each title with
    /// its <c>number</c>, <c>name</c> and <c>colour</c>; or null when the reply is not one.
    /// </summary>
    internal static byte[]? FileFrom(ReadOnlySpan<byte> reply)
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
                // The game's own titles, which the client names itself, and the entry that
                // describes a series of them, with no number, are not the file's.
                if (title.ValueKind != JsonValueKind.Object || !title.TryGetProperty("number", out var number) ||
                    number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out int n) || n is < First or > Last)
                    continue;
                uint colour = title.TryGetProperty("colour", out var c) && c.ValueKind == JsonValueKind.Number &&
                              c.TryGetUInt32(out uint value) ? value : DefaultColour;
                // A hidden title's name is null to a launcher, which asks without a session.
                string? name = title.TryGetProperty("name", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                own.Add(new OwnTitle(n, colour, name));
            }
            return Build(own);
        }
    }

    /// <summary>One of the server's own titles: its number, its plate colour and its name, if it has one.</summary>
    internal readonly record struct OwnTitle(int Number, uint Colour, string? Name);

    /// <summary>
    /// The file for <paramref name="titles"/>, numbers from 0x1000 to 0x1fff; the first given of
    /// a number counts.
    /// </summary>
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

    /// <summary>
    /// Whether <paramref name="name"/> can go into the file: 1 to 59 characters of printable
    /// ASCII, none of them &lt;, &gt; or &amp;, which the client's markup reads (sub_598700), and no
    /// space at either end, as the server's name_problem has it. A blank or padded name would draw an
    /// empty or lopsided plate where "Unknown" is at least honest.
    /// </summary>
    internal static bool Fits([NotNullWhen(true)] string? name) =>
        name is { Length: > 0 and <= NameMost } && name[0] != ' ' && name[^1] != ' '
        && name.All(c => c is >= ' ' and <= '~' and not ('<' or '>' or '&'));
}
