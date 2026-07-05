namespace TacticalDisplay.App.Security;

public interface ISecureTokenStore
{
    Task<string?> GetSessionTokenAsync();
    Task SetSessionTokenAsync(string token);
    Task ClearSessionTokenAsync();
}
