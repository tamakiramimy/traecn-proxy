using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace TrancnProxy;

public sealed class TraeOAuthLoginManager
{
    private readonly ConcurrentDictionary<string, PendingLogin> _pending = new(StringComparer.Ordinal);

    public string Begin(string alias, string callbackUrl, string deviceId, string machineId,
        int maxConcurrency = TraeConcurrencyLimits.Default)
    {
        if (string.IsNullOrWhiteSpace(alias)) throw new InvalidOperationException("账号别名不能为空。");
        if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out _)) throw new InvalidOperationException("public base URL 无效。");
        TraeConcurrencyLimits.Validate(maxConcurrency);

        string state = ToUrlSafe(RandomNumberGenerator.GetBytes(32));
        string verifier = ToUrlSafe(RandomNumberGenerator.GetBytes(48));
        string challenge = ToUrlSafe(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        // 上游回调不会回传 state，只原样回传 login_trace_id，因此用它停放待处理登录。
        string loginTraceId = Guid.NewGuid().ToString();
        _pending[loginTraceId] = new PendingLogin(alias, verifier, deviceId, machineId, maxConcurrency, DateTimeOffset.UtcNow.AddMinutes(5));
        RemoveExpired();

        var query = new Dictionary<string, string>
        {
            ["login_version"] = "1",
            ["auth_from"] = "trae",
            ["login_channel"] = "native_ide",
            ["plugin_version"] = "2.3.72447",
            ["auth_type"] = "local",
            ["client_id"] = TraeClient.DefaultClientId,
            ["redirect"] = "0",
            ["login_trace_id"] = loginTraceId,
            ["auth_callback_url"] = callbackUrl,
            ["state"] = state,
            ["machine_id"] = machineId,
            ["device_id"] = deviceId,
            ["x_device_id"] = deviceId,
            ["x_machine_id"] = machineId,
            ["x_device_type"] = OperatingSystem.IsMacOS() ? "mac" : OperatingSystem.IsWindows() ? "windows" : "linux",
            ["x_os_version"] = $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version}",
            ["x_app_version"] = "3.3.90",
            ["x_app_type"] = "stable",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        return "https://console.enterprise.trae.cn/authorization?" +
               string.Join("&", query.Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value)}"));
    }

    public async Task<TraeAccount> CompleteAsync(IQueryCollection query, CancellationToken ct)
    {
        if (!TryTakePending(query, out var pending))
            throw new InvalidOperationException("网页登录状态无效或已过期，请重新发起登录。");

        string refreshToken = query["refreshToken"].ToString();
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new InvalidOperationException("授权回调缺少 refreshToken。");
        string apiHost = query["host"].ToString();
        if (string.IsNullOrWhiteSpace(apiHost)) apiHost = "https://console.enterprise.trae.cn";
        string consoleHost = query["consoleHost"].ToString();
        if (string.IsNullOrWhiteSpace(consoleHost)) consoleHost = "https://console.enterprise.trae.cn";

        var bootstrap = new TraeAuthData { ApiHost = apiHost, ConsoleHost = consoleHost };
        var client = new TraeClient(bootstrap, pending.DeviceId, pending.MachineId);
        var tokenData = await client.ExchangeTokenAsync(refreshToken, ct);
        var userInfo = await client.GetUserInfoAsync(tokenData.Token, ct);
        var auth = new TraeAuthData
        {
            Token = tokenData.Token,
            RefreshToken = tokenData.RefreshToken,
            ExpiredAt = ParseDate(tokenData.TokenExpireAt, tokenData.TokenExpireDurationMs),
            RefreshExpiredAt = ParseDate(tokenData.RefreshExpireAt, null),
            TokenReleaseAt = DateTimeOffset.UtcNow,
            UserId = (string?)userInfo["Data"]?["UserInfo"]?["UserID"] ?? "",
            Username = (string?)userInfo["Data"]?["UserInfo"]?["Name"] ?? "",
            Email = (string?)userInfo["Data"]?["UserInfo"]?["Email"] ?? "",
            ApiHost = apiHost,
            ConsoleHost = consoleHost,
            Standalone = true
        };
        return new TraeAccount
        {
            Alias = pending.Alias,
            MaxConcurrency = pending.MaxConcurrency,
            Auth = auth,
            DeviceId = pending.DeviceId,
            MachineId = pending.MachineId
        };
    }

    private void RemoveExpired()
    {
        foreach (var item in _pending.Where(x => x.Value.ExpiresAt <= DateTimeOffset.UtcNow))
            _pending.TryRemove(item.Key, out _);
    }

    /// <summary>Consumes the pending login matching the callback's trace identifier.</summary>
    /// <param name="query">The OAuth callback query.</param>
    /// <param name="pending">The matching pending login when found.</param>
    /// <returns><see langword="true"/> when an unexpired pending login was consumed.</returns>
    internal bool TryTakePending(IQueryCollection query, out PendingLogin pending)
    {
        pending = default!;
        string key = new[] { "loginTraceID", "login_trace_id", "state" }
            .Select(name => query[name].ToString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        if (string.IsNullOrWhiteSpace(key) || !_pending.TryRemove(key, out var found)) return false;
        if (found.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        pending = found;
        return true;
    }

    private static string ToUrlSafe(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static DateTimeOffset? ParseDate(string? value, long? durationMs)
    {
        if (!string.IsNullOrWhiteSpace(value) && DateTimeOffset.TryParse(value, out var date))
        {
            if (durationMs is { } duration && date < DateTimeOffset.UtcNow && duration > 0)
                return DateTimeOffset.UtcNow.AddMilliseconds(duration);
            return date;
        }
        return long.TryParse(value, out var milliseconds) && milliseconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
    }

    internal sealed record PendingLogin(string Alias, string Verifier, string DeviceId, string MachineId,
        int MaxConcurrency, DateTimeOffset ExpiresAt);
}