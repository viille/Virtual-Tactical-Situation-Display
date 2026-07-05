using System.Net;

namespace TacticalDisplay.App.Cloud;

public sealed class CloudApiException : Exception
{
    public CloudApiException(string message, HttpStatusCode? statusCode = null, string? errorCode = null, bool isNetworkError = false, Exception? inner = null)
        : base(message, inner) { StatusCode = statusCode; ErrorCode = errorCode; IsNetworkError = isNetworkError; }
    public HttpStatusCode? StatusCode { get; }
    public string? ErrorCode { get; }
    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;
    public bool IsForbidden => StatusCode == HttpStatusCode.Forbidden;
    public bool IsRateLimited => StatusCode == HttpStatusCode.TooManyRequests;
    public bool IsNetworkError { get; }
}
