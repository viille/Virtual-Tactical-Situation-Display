using System.Net;
using System.Text;
using System.Text.Json;
using TacticalDisplay.App.Data;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class XPlane12WebApiClientTests
{
    [Fact]
    public async Task SelectsKnownV1ContractWhenCapabilitiesAlsoAdvertiseUnknownVersion()
    {
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/capabilities" => Json(HttpStatusCode.OK, "{\"api\":{\"versions\":[\"v1\",\"v2\"]}}"),
            "/api/v1/datarefs" => Json(HttpStatusCode.OK, "{\"data\":[{\"id\":42}]}"),
            "/api/v1/datarefs/42/value" when request.Method == HttpMethod.Get => Json(HttpStatusCode.OK, "{\"data\":[1,2,3]}"),
            "/api/v1/datarefs/42/value" when request.Method.Method == "PATCH" => Json(HttpStatusCode.OK, "{\"data\":null}"),
            _ => Json(HttpStatusCode.NotFound, "{}")
        });
        using var client = new XPlane12WebApiClient("http://xp.test", new HttpClient(handler));

        await client.DiscoverApiVersionAsync(CancellationToken.None);
        var id = await client.ResolveDataRefIdAsync("sim/test/array", CancellationToken.None);
        using var read = await client.GetValueDocumentAsync(id, CancellationToken.None);
        await client.PatchValueAsync(id, new[] { 3d, 2d, 1d }, CancellationToken.None);

        Assert.Equal("v1", client.ApiVersion);
        Assert.Equal(42, id);
        Assert.Equal(3, read.RootElement.GetProperty("data").GetArrayLength());
        Assert.Equal(new[] { "GET /api/capabilities", "GET /api/v1/datarefs", "GET /api/v1/datarefs/42/value", "PATCH /api/v1/datarefs/42/value" }, handler.Requests);
        Assert.Equal("{\"data\":[3,2,1]}", handler.Bodies.Last());
    }

    [Fact]
    public async Task CapabilitiesWithOnlyUnknownVersionsFailClosed()
    {
        using var client = new XPlane12WebApiClient("http://xp.test", new HttpClient(new FakeHandler(_ =>
            Json(HttpStatusCode.OK, "{\"api\":{\"versions\":[\"v2\",\"v3\"]}}"))));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DiscoverApiVersionAsync(CancellationToken.None));

        Assert.Contains("supported v1 contract", error.Message);
    }

    [Fact]
    public async Task MissingDataRefAndForbiddenApiFailClosed()
    {
        using var missing = new XPlane12WebApiClient("http://xp.test", new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.OK, "{\"data\":[]}"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.ResolveDataRefIdAsync("missing", CancellationToken.None));

        using var forbidden = new XPlane12WebApiClient("http://xp.test", new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.Forbidden, "{}"))));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => forbidden.DiscoverApiVersionAsync(CancellationToken.None));
        Assert.Contains("Network security", error.Message);
    }

    [Fact]
    public async Task CapabilitiesNotFoundFallsBackToV1()
    {
        using var client = new XPlane12WebApiClient("http://xp.test", new HttpClient(new FakeHandler(_ => Json(HttpStatusCode.NotFound, "{}"))));

        await client.DiscoverApiVersionAsync(CancellationToken.None);

        Assert.Equal("v1", client.ApiVersion);
    }

    [Fact]
    public async Task MalformedCapabilitiesAndDataRefResponsesFailClosed()
    {
        using var malformedCapabilities = new XPlane12WebApiClient("http://xp.test", new HttpClient(
            new FakeHandler(_ => Json(HttpStatusCode.OK, "{broken"))));
        await Assert.ThrowsAnyAsync<JsonException>(() => malformedCapabilities.DiscoverApiVersionAsync(CancellationToken.None));

        using var malformedDataRefs = new XPlane12WebApiClient("http://xp.test", new HttpClient(
            new FakeHandler(_ => Json(HttpStatusCode.OK, "{\"data\":[{\"id\":\"bad\"}]}"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => malformedDataRefs.ResolveDataRefIdAsync("test", CancellationToken.None));
    }

    [Fact]
    public async Task RequestTimeoutPropagatesToCaller()
    {
        using var client = new XPlane12WebApiClient("http://xp.test", new HttpClient(new TimeoutHandler()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DiscoverApiVersionAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(20)).Token));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
