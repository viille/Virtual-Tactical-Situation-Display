using TacticalDisplay.App.Security;

namespace TacticalDisplay.App.Cloud;

public sealed class AuthService
{
    private readonly VtsdCloudClient _client; private readonly DeviceLoginService _deviceLogin; private readonly ISecureTokenStore _tokens; private readonly ICloudTelemetryService _telemetry;
    public AuthService(VtsdCloudClient client, DeviceLoginService deviceLogin, ISecureTokenStore tokens, ICloudTelemetryService telemetry) { _client = client; _deviceLogin = deviceLogin; _tokens = tokens; _telemetry = telemetry; }
    public AuthState State { get; private set; } = new(AuthStatus.SignedOut);
    public event EventHandler<AuthState>? StateChanged;

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(await _tokens.GetSessionTokenAsync().ConfigureAwait(false))) { Set(new(AuthStatus.SignedOut)); return; }
        try { var me = await _client.GetMeAsync(ct).ConfigureAwait(false); Set(new(AuthStatus.SignedIn, me.User)); }
        catch (CloudApiException ex) when (ex.IsUnauthorized) { await _telemetry.TrackApiErrorAsync(ex).ConfigureAwait(false); await _tokens.ClearSessionTokenAsync().ConfigureAwait(false); Set(new(AuthStatus.SessionExpired)); }
        catch (CloudApiException ex) when (ex.IsNetworkError) { await _telemetry.TrackApiErrorAsync(ex).ConfigureAwait(false); Set(new(AuthStatus.OfflineUnknown)); }
    }

    public async Task SignInAsync(Action<Uri> openBrowser, CancellationToken ct)
    {
        Set(new(AuthStatus.SigningIn));
        await _telemetry.TrackAsync("cloud_login_started", ct: ct).ConfigureAwait(false);
        try
        {
            var status = await _deviceLogin.SignInAsync(openBrowser, ct).ConfigureAwait(false);
            await _tokens.SetSessionTokenAsync(status.SessionToken!).ConfigureAwait(false);
            var me = await _client.GetMeAsync(ct).ConfigureAwait(false); Set(new(AuthStatus.SignedIn, me.User));
            await _telemetry.TrackAsync("cloud_login_completed", ct: ct).ConfigureAwait(false);
        }
        catch (CloudApiException ex)
        {
            await _telemetry.TrackApiErrorAsync(ex).ConfigureAwait(false);
            await _telemetry.TrackAsync("cloud_login_failed", ct: CancellationToken.None).ConfigureAwait(false);
            if (State.Status == AuthStatus.SigningIn) Set(new(AuthStatus.SignedOut)); throw;
        }
        catch { await _telemetry.TrackAsync("cloud_login_failed", ct: CancellationToken.None).ConfigureAwait(false); if (State.Status == AuthStatus.SigningIn) Set(new(AuthStatus.SignedOut)); throw; }
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        try { await _client.LogoutAsync(ct).ConfigureAwait(false); } catch (CloudApiException ex) when (ex.IsNetworkError || ex.IsUnauthorized) { }
        finally { await _tokens.ClearSessionTokenAsync().ConfigureAwait(false); Set(new(AuthStatus.SignedOut)); }
    }
    private void Set(AuthState state) { State = state; StateChanged?.Invoke(this, state); }
}
