using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RestaurantOrders.Cloud.Api;

public sealed class LicenseManagerSessions
{
    public const int IdleTimeoutSeconds = 10 * 60;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(IdleTimeoutSeconds);
    private const string CookieName = "restaurant_license_manager";
    private const string CookiePath = "/api/license-manager/v1";
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);

    public void Create(HttpContext context, string email)
    {
        RemoveExpiredSessions(DateTime.UtcNow.Ticks);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        sessions[HashToken(token)] = new Session(email, DateTime.UtcNow.Ticks);
        WriteCookie(context, token);
    }

    public bool TryRefresh(HttpContext context, out string email)
    {
        email = "";
        var token = context.Request.Cookies[CookieName];
        if (string.IsNullOrWhiteSpace(token))
        {
            ClearCookie(context);
            return false;
        }

        var key = HashToken(token);
        if (!sessions.TryGetValue(key, out var session))
        {
            ClearCookie(context);
            return false;
        }

        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref session.LastActivityTicks) >= IdleTimeout.Ticks)
        {
            sessions.TryRemove(key, out _);
            ClearCookie(context);
            return false;
        }

        Interlocked.Exchange(ref session.LastActivityTicks, now);
        email = session.Email;
        WriteCookie(context, token);
        return true;
    }

    public string? GetEmail(HttpContext context)
    {
        var token = context.Request.Cookies[CookieName];
        return string.IsNullOrWhiteSpace(token) || !sessions.TryGetValue(HashToken(token), out var session)
            ? null
            : session.Email;
    }

    public void Revoke(HttpContext context)
    {
        var token = context.Request.Cookies[CookieName];
        if (!string.IsNullOrWhiteSpace(token))
            sessions.TryRemove(HashToken(token), out _);
        ClearCookie(context);
    }

    private void RemoveExpiredSessions(long nowTicks)
    {
        foreach (var pair in sessions)
            if (nowTicks - Interlocked.Read(ref pair.Value.LastActivityTicks) >= IdleTimeout.Ticks)
                sessions.TryRemove(pair.Key, out _);
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static void WriteCookie(HttpContext context, string token) =>
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            MaxAge = IdleTimeout,
            IsEssential = true
        });

    private static void ClearCookie(HttpContext context) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = CookiePath,
            IsEssential = true
        });

    private sealed class Session(string email, long lastActivityTicks)
    {
        public string Email { get; } = email;
        public long LastActivityTicks = lastActivityTicks;
    }
}
