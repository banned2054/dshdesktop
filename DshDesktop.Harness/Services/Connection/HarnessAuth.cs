using DshDesktop.Harness.Exceptions;
using System.Net;

namespace DshDesktop.Harness.Services.Connection;

/// <summary>
///     一次性启动令牌交换：GET /?token=... 由后端签发会话 Cookie，
///     之后所有 HTTP 与 WebSocket 请求仅凭该 Cookie 认证。
///     HttpClient 必须配置 AllowAutoRedirect=false 且使用共享 CookieContainer。
/// </summary>
public static class HarnessAuth
{
    public static async Task AuthenticateAsync(
        HttpClient httpClient, Uri authenticatedUri, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(authenticatedUri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.SeeOther or HttpStatusCode.Found or HttpStatusCode.OK))
            throw new HarnessConnectionException($"后端认证交换失败：HTTP {(int)response.StatusCode}。请确认使用的是本次启动返回的地址。");

        var issuedCookie = response.Headers.TryGetValues("Set-Cookie", out var cookies) &&
                           cookies.Any();
        if (!issuedCookie && response.StatusCode != HttpStatusCode.OK)
            throw new HarnessConnectionException("后端认证交换未签发会话 Cookie。");
    }
}
