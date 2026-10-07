using System.Net;
using System.Text;
using System.Text.Json;
using TacticalDisplay.App.Data;
using Xunit;

namespace TacticalDisplay.Tests;

public sealed class XPlane12AarFuelAdapterTests
{
    [Fact]
    public async Task FuelMutationUsesKgTankDistributionAndVerifiedReadback()
    {
        using var simulator = new FakeXPlaneWebApi();
        using var api = new XPlane12WebApiClient("http://xp.test/", simulator.CreateClient());
        var adapter = new XPlane12AarFuelAdapter(api, () => true);
        await adapter.InitializeAsync(CancellationToken.None);

        Assert.True(adapter.CanReadFuel);
        Assert.True(adapter.CanWriteFuel);
        var before = adapter.ReadFuel()!.CurrentFuelKg;
        var plus = await adapter.ApplyFuelDeltaKgAsync(100, CancellationToken.None);
        var minus = await adapter.ApplyFuelDeltaKgAsync(-100, CancellationToken.None);

        Assert.Equal(AarFuelApplyStatus.Success, plus.Status);
        Assert.Equal(100, plus.AppliedKg, 1);
        Assert.Equal(AarFuelApplyStatus.Success, minus.Status);
        Assert.Equal(-100, minus.AppliedKg, 1);
        Assert.Equal(before, adapter.ReadFuel()!.CurrentFuelKg, 1);
        Assert.True(simulator.PatchCount >= 3);
    }

    [Fact]
    public async Task FuelMutationClampsToTankCapacityAndReportsPartialApplication()
    {
        using var simulator = new FakeXPlaneWebApi();
        simulator.SetFuel(Enumerable.Repeat(110d, 9).ToArray());
        using var api = new XPlane12WebApiClient("http://xp.test/", simulator.CreateClient());
        var adapter = new XPlane12AarFuelAdapter(api, () => true);
        await adapter.InitializeAsync(CancellationToken.None);

        var result = await adapter.ApplyFuelDeltaKgAsync(100, CancellationToken.None);

        Assert.Equal(AarFuelApplyStatus.Partial, result.Status);
        Assert.InRange(result.AppliedKg, 9, 11);
        Assert.Equal(1000, adapter.ReadFuel()!.CurrentFuelKg, 1);
    }

    [Fact]
    public async Task ReadOnlyOrOverwrittenFuelDataRefsNeverRemainWritable()
    {
        using var readOnlySimulator = new FakeXPlaneWebApi { RejectWrites = true };
        using var readOnlyApi = new XPlane12WebApiClient("http://xp.test/", readOnlySimulator.CreateClient());
        var readOnlyAdapter = new XPlane12AarFuelAdapter(readOnlyApi, () => true);
        await readOnlyAdapter.InitializeAsync(CancellationToken.None);
        Assert.True(readOnlyAdapter.CanReadFuel);
        Assert.False(readOnlyAdapter.CanWriteFuel);

        using var overwrittenSimulator = new FakeXPlaneWebApi { IgnoreWrites = true };
        using var overwrittenApi = new XPlane12WebApiClient("http://xp.test/", overwrittenSimulator.CreateClient());
        var overwrittenAdapter = new XPlane12AarFuelAdapter(overwrittenApi, () => true);
        await overwrittenAdapter.InitializeAsync(CancellationToken.None);
        Assert.True(overwrittenAdapter.CanWriteFuel);

        var result = await overwrittenAdapter.ApplyFuelDeltaKgAsync(10, CancellationToken.None);

        Assert.Equal(AarFuelApplyStatus.Failed, result.Status);
        Assert.Equal(0, result.AppliedKg);
        Assert.False(overwrittenAdapter.CanWriteFuel);
    }

    private sealed class FakeXPlaneWebApi : IDisposable
    {
        private static readonly Dictionary<string, long> Ids = new(StringComparer.Ordinal)
        {
            ["sim/flightmodel/weight/m_fuel"] = 1,
            ["sim/flightmodel/weight/m_fuel_total"] = 2,
            ["sim/aircraft/weight/acf_m_fuel_tot"] = 3,
            ["sim/aircraft/overflow/acf_tank_rat"] = 4
        };
        private readonly HttpClient _client;
        private double[] _fuel = Enumerable.Repeat(50d, 9).ToArray();
        public bool RejectWrites { get; init; }
        public bool IgnoreWrites { get; init; }
        public int PatchCount { get; private set; }

        public FakeXPlaneWebApi() => _client = new HttpClient(new Handler(this));
        public HttpClient CreateClient() => _client;
        public void SetFuel(double[] fuel) => _fuel = (double[])fuel.Clone();
        public void Dispose() => _client.Dispose();

        private HttpResponseMessage Send(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/capabilities") return Json(HttpStatusCode.OK, "{\"api\":{\"versions\":[\"v1\"]}} ");
            if (path == "/api/v1/datarefs" && request.Method == HttpMethod.Get)
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?'));
                var name = query[(query.IndexOf('=') + 1)..];
                return Ids.TryGetValue(name, out var id)
                    ? Json(HttpStatusCode.OK, $"{{\"data\":[{{\"id\":{id}}}]}}")
                    : Json(HttpStatusCode.OK, "{\"data\":[]}");
            }
            if (path.StartsWith("/api/v1/datarefs/", StringComparison.Ordinal) && path.EndsWith("/value", StringComparison.Ordinal))
            {
                var pieces = path.Split('/');
                var id = long.Parse(pieces[^2]);
                if (request.Method == HttpMethod.Get)
                {
                    var value = id switch
                    {
                        1 => JsonSerializer.Serialize(_fuel),
                        2 => _fuel.Sum().ToString(System.Globalization.CultureInfo.InvariantCulture),
                        3 => "2204.62262185",
                        4 => JsonSerializer.Serialize(Enumerable.Repeat(1d / 9, 9).ToArray()),
                        _ => "null"
                    };
                    return Json(HttpStatusCode.OK, $"{{\"data\":{value}}}");
                }
                PatchCount++;
                if (RejectWrites) return Json(HttpStatusCode.Forbidden, "{\"error_code\":\"dataref_is_readonly\"}");
                if (!IgnoreWrites && id == 1)
                {
                    using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    _fuel = body.RootElement.GetProperty("data").EnumerateArray().Select(item => item.GetDouble()).ToArray();
                }
                return Json(HttpStatusCode.OK, "{}");
            }
            return Json(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string value) => new(status)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };

        private sealed class Handler(FakeXPlaneWebApi owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(owner.Send(request));
            }
        }
    }
}
