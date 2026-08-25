using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrancnProxy;

/// <summary>
/// 独立网页授权(不依赖 Trae CN IDE 的本地登录态):
/// 1. 本地起一个 127.0.0.1 随机端口的回调服务器
/// 2. 打开浏览器到 consoleHost 的 /authorization(PKCE 参数与 IDE 一致)
/// 3. 登录完成后页面跳回本地回调,携带 refreshToken/consoleHost/host
/// 4. 用 refreshToken 调 ExchangeToken 换 access token,再 GetUserInfo 补全账号信息
/// </summary>
public static class StandaloneLogin
{
    public static async Task<TraeAuthData> LoginAsync(TraeClient client, string machineId, string deviceId,
        int timeoutSeconds = 300, CancellationToken ct = default)
    {
        // ---------- 1. PKCE ----------
        byte[] verifierBytes = RandomNumberGenerator.GetBytes(48);
        string codeVerifier = Convert.ToBase64String(verifierBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string codeChallenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        // ---------- 2. 本地回调服务器 ----------
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var callback = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        _ = Task.Run(async () =>
        {
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    TcpClient sock;
                    try { sock = await listener.AcceptTcpClientAsync(timeout.Token); }
                    catch (OperationCanceledException) { return; }

                    _ = Task.Run(async () =>
                    {
                        using (sock)
                        await using (var stream = sock.GetStream())
                        {
                            var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, leaveOpen: true);
                            string? line = await reader.ReadLineAsync();
                            if (line is null) return;
                            string[] parts = line.Split(' ');
                            string path = parts.Length > 1 ? parts[1] : "/";
                            string body = path.StartsWith("/authorize")
                                ? "<html><body style=\"font-family:system-ui;text-align:center;padding-top:80px\"><h2>授权完成 ✅</h2><p>可以关闭此页面返回终端。</p></body></html>"
                                : "<html><body>not found</body></html>";
                            var bytes = Encoding.UTF8.GetBytes(body);
                            var head = Encoding.UTF8.GetBytes($"HTTP/1.1 {(path.StartsWith("/authorize") ? 200 : 404)} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(head);
                            await stream.WriteAsync(bytes);
                            if (path.StartsWith("/authorize"))
                                callback.TrySetResult(path);
                        }
                    }, timeout.Token);
                }
            }
            catch { }
            finally { listener.Stop(); }
        });

        // ---------- 3. 登录 URL(与 IDE loginUrlBuilder 一致) ----------
        string consoleHost = client.ApiHost.TrimEnd('/');
        var q = new Dictionary<string, string>
        {
            ["login_version"] = "1",
            ["auth_from"] = "trae",
            ["login_channel"] = "native_ide",
            ["plugin_version"] = "2.3.72447",
            ["auth_type"] = "local",
            ["client_id"] = "ono9krqynydwx5",
            ["redirect"] = "0",
            ["login_trace_id"] = Guid.NewGuid().ToString(),
            ["auth_callback_url"] = $"http://127.0.0.1:{port}/authorize",
            ["machine_id"] = machineId,
            ["device_id"] = deviceId,
            ["x_device_id"] = deviceId,
            ["x_machine_id"] = machineId,
            ["x_device_brand"] = DeviceBrand(),
            ["x_device_type"] = OperatingSystem.IsMacOS() ? "mac" : OperatingSystem.IsWindows() ? "windows" : "linux",
            ["x_os_version"] = $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version}",
            ["x_env"] = "",
            ["x_app_version"] = "3.3.90",
            ["x_app_type"] = "stable",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        string loginUrl = consoleHost + "/authorization?" +
            string.Join("&", q.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        Console.WriteLine();
        Console.WriteLine("=== 网页授权 ===");
        Console.WriteLine("浏览器即将打开登录页,请完成登录。");
        Console.WriteLine($"(5 分钟内有效) 登录地址:");
        Console.WriteLine($"  {loginUrl[..Math.Min(120, loginUrl.Length)]}...");
        Console.WriteLine();

        OpenBrowser(loginUrl);

        // ---------- 4. 等待回调 ----------
        string callbackPath;
        try
        {
            callbackPath = await callback.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"网页授权超时({timeoutSeconds / 60} 分钟)");
        }

        var query = ParseQuery(callbackPath);
        string refreshToken = Uri.UnescapeDataString(GetParam(query, "refreshToken"));
        if (string.IsNullOrEmpty(refreshToken))
            throw new InvalidDataException("回调中未找到 refreshToken,授权可能失败");

        string cbHost = Uri.UnescapeDataString(GetParam(query, "host"));
        string cbConsole = Uri.UnescapeDataString(GetParam(query, "consoleHost"));
        string apiHost = string.IsNullOrWhiteSpace(cbHost) ? consoleHost : cbHost;
        string console = string.IsNullOrWhiteSpace(cbConsole) ? consoleHost : cbConsole;
        Console.WriteLine($"回调收到授权,上游 host: {apiHost}");

        // ---------- 5. 换 token + 拉取用户信息 ----------
        var tokenData = await client.ExchangeTokenAsync(refreshToken, ct);
        var userInfo = await client.GetUserInfoAsync(tokenData.Token, ct);

        var auth = new TraeAuthData
        {
            Token = tokenData.Token,
            RefreshToken = tokenData.RefreshToken,
            ExpiredAt = ParseDate(tokenData.TokenExpireAt, tokenData.TokenExpireDurationMs),
            RefreshExpiredAt = ParseDate(tokenData.RefreshExpireAt, null),
            TokenReleaseAt = DateTimeOffset.UtcNow,
            UserId = (string?)userInfo?["Data"]?["UserInfo"]?["UserID"] ?? "",
            Username = (string?)userInfo?["Data"]?["UserInfo"]?["Name"] ?? "",
            Email = (string?)userInfo?["Data"]?["UserInfo"]?["Email"] ?? "",
            ApiHost = apiHost,
            ConsoleHost = console,
            Standalone = true
        };
        Console.WriteLine($"授权成功: {auth.Username}/{auth.Email} token过期: {auth.ExpiredAt:yyyy-MM-dd HH:mm}Z");
        return auth;
    }

    private static Dictionary<string, string> ParseQuery(string path)
    {
        var result = new Dictionary<string, string>();
        int i = path.IndexOf('?');
        if (i < 0) return result;
        foreach (string pair in path[(i + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0)
                result[pair[..eq]] = pair[(eq + 1)..];
            else if (pair.Length > 0)
                result[pair] = "";
        }
        return result;
    }

    private static string GetParam(Dictionary<string, string> q, string name) =>
        q.TryGetValue(name, out var v) ? v : "";

    private static DateTimeOffset? ParseDate(string? value, long? durationMs)
    {
        if (!string.IsNullOrWhiteSpace(value) && DateTimeOffset.TryParse(value, out var d))
        {
            if (durationMs is { } dur && d < DateTimeOffset.UtcNow && dur > 0)
                return DateTimeOffset.UtcNow.AddMilliseconds(dur);
            return d;
        }
        if (long.TryParse(value, out var ms) && ms > 0)
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return null;
    }

    private static string DeviceBrand()
    {
        try
        {
            var psi = new ProcessStartInfo("sysctl", "-n hw.model") { RedirectStandardOutput = true };
            var p = Process.Start(psi);
            return (p?.StandardOutput.ReadToEnd().Trim() ?? "Mac");
        }
        catch { return OperatingSystem.IsMacOS() ? "Mac" : "PC"; }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = true });
            else if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("cmd", $"/c start \"\" \"{url}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"自动打开浏览器失败({ex.Message}),请手动复制上面的登录地址访问");
        }
    }
}
