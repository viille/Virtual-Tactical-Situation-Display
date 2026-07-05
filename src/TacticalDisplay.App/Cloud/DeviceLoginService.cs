namespace TacticalDisplay.App.Cloud;

public sealed class DeviceLoginService
{
    private readonly VtsdCloudClient _client; private readonly TimeSpan _pollInterval;
    public DeviceLoginService(VtsdCloudClient client, TimeSpan? pollInterval = null) { _client = client; _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3); }

    public async Task<DeviceLoginStatusResponse> SignInAsync(Action<Uri> openBrowser, CancellationToken ct)
    {
        var start = await _client.StartDeviceLoginAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(start.RequestId) || string.IsNullOrWhiteSpace(start.RequestToken) ||
            !Uri.TryCreate(start.LoginUrl, UriKind.Absolute, out var loginUri) || loginUri.Scheme != Uri.UriSchemeHttps ||
            start.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new CloudApiException("VTSD Cloud returned an invalid device login response.", errorCode: "MALFORMED_RESPONSE");

        openBrowser(loginUri);
        while (DateTimeOffset.UtcNow < start.ExpiresAt)
        {
            await Task.Delay(GetPollDelay(start), ct).ConfigureAwait(false);
            var status = await _client.GetDeviceLoginStatusAsync(start.RequestId, start.RequestToken, ct).ConfigureAwait(false);
            switch (status.Status.ToLowerInvariant())
            {
                case "pending": continue;
                case "completed" when !string.IsNullOrWhiteSpace(status.SessionToken): return status;
                case "completed": throw new CloudApiException("VTSD Cloud omitted the completed session token.", errorCode: "MALFORMED_RESPONSE");
                case "expired": throw new CloudApiException("The login request expired.", errorCode: "LOGIN_EXPIRED");
                case "failed": throw new CloudApiException(status.Message ?? "VATSIM login failed.", errorCode: "LOGIN_FAILED");
                default: throw new CloudApiException("VTSD Cloud returned an unknown login state.", errorCode: "MALFORMED_RESPONSE");
            }
        }
        throw new CloudApiException("The login request expired.", errorCode: "LOGIN_EXPIRED");
    }

    private TimeSpan GetPollDelay(DeviceLoginStartResponse start)
    {
        var seconds = start.PollIntervalSeconds ?? start.IntervalSeconds ?? start.PollInterval;
        return seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : _pollInterval;
    }
}
