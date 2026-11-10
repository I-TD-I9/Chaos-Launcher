using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Chaos.Tests;

public class ServerInfoTests
{
    static readonly Uri Api = new("https://api.example.test/");

    sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Seen.Add((request.Method, request.RequestUri));
            return Task.FromResult(answer(request));
        }
    }

    static ServerInfo? Info(string rest)
    {
        using var json = JsonDocument.Parse($$"""{"ok": true{{rest}}}""");
        return ServerInfo.FromInfo(json.RootElement);
    }

    static HttpResponseMessage Reply(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // A reply labelled with a charset .NET has no decoder for, as a filtering proxy's page may be.
    static HttpResponseMessage Labelled(HttpStatusCode status, string body, string contentType)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    [Fact]
    public void The_oldest_launcher_comes_from_the_launcher_minimum()
    {
        var info = Info(""", "launcher": {"min": "0.2.0"}""")!;
        Assert.Equal(new Version(0, 2, 0), info.OldestLauncher);
    }

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("0.1.9", true)]
    [InlineData("0.2.0", false)]
    [InlineData("0.10.0", false)]
    [InlineData("1.0.0", false)]
    public void A_launcher_older_than_the_oldest_is_too_old(string version, bool tooOld) =>
        Assert.Equal(tooOld, Info(""", "launcher": {"min": "0.2.0"}""")!.TooOld(Version.Parse(version)));

    [Theory]
    [InlineData("")]
    [InlineData(""", "launcher": {"min": null}""")]
    [InlineData(""", "launcher": {"min": "soon"}""")]
    [InlineData(""", "launcher": "0.2.0" """)]
    public void Without_a_usable_oldest_any_launcher_will_do(string launcher)
    {
        var info = Info(launcher)!;
        Assert.Null(info.OldestLauncher);
        Assert.False(info.TooOld(new Version(0, 1, 0)));
    }

    [Fact]
    public void A_minimum_without_a_patch_number_reads_as_patch_zero() =>
        Assert.False(Info(""", "launcher": {"min": "0.2"}""")!.TooOld(new Version(0, 2, 0)));

    [Fact]
    public void What_the_server_says_about_accounts_does_not_matter_to_it() =>
        Assert.Equal(new ServerInfo(new Version(0, 2, 0)),
                     Info(""", "registration": "open", "discord": {"enabled": true}, "email_signup": false, "launcher": {"min": "0.2.0"}"""));

    [Theory]
    [InlineData("""{"ok": false}""")]
    [InlineData("""{"launcher": {"min": "0.2.0"}}""")]
    [InlineData("""["ok"]""")]
    public void A_reply_that_is_not_an_info_gives_none(string body)
    {
        using var json = JsonDocument.Parse(body);
        Assert.Null(ServerInfo.FromInfo(json.RootElement));
    }

    [Fact]
    public async Task The_info_comes_from_api_info()
    {
        var server = new FakeServer(_ => Reply(HttpStatusCode.OK, """{"ok": true, "launcher": {"min": "0.5.2"}}"""));
        var info = await ServerInfo.FetchAsync(new HttpClient(server), Api);
        Assert.Equal(new ServerInfo(new Version(0, 5, 2)), info);
        Assert.Equal((HttpMethod.Get, new Uri("https://api.example.test/api/info")), server.Seen.Single());
    }

    [Theory]
    [InlineData("windows-1252")]
    [InlineData("shift_jis")]
    [InlineData("no-such-charset")]
    public async Task A_charset_dotnet_cannot_decode_is_read_as_UTF8(string charset)
    {
        var server = new FakeServer(_ => Labelled(HttpStatusCode.OK, """{"ok": true, "launcher": {"min": "0.5.2"}}""",
                                                  "application/json; charset=" + charset));
        Assert.Equal(new ServerInfo(new Version(0, 5, 2)), await ServerInfo.FetchAsync(new HttpClient(server), Api));
    }

    [Fact]
    public async Task A_proxys_error_page_gives_none()
    {
        var server = new FakeServer(_ => Labelled(HttpStatusCode.OK, "<html>Blocked by policy</html>", "text/html; charset=shift_jis"));
        Assert.Null(await ServerInfo.FetchAsync(new HttpClient(server), Api));
    }

    [Fact]
    public async Task An_error_status_gives_none()
    {
        var server = new FakeServer(_ => Reply(HttpStatusCode.InternalServerError, """{"ok": false}"""));
        Assert.Null(await ServerInfo.FetchAsync(new HttpClient(server), Api));
    }

    [Fact]
    public async Task No_connection_gives_none()
    {
        var server = new FakeServer(_ => throw new HttpRequestException("no route"));
        Assert.Null(await ServerInfo.FetchAsync(new HttpClient(server), Api));
    }
}
