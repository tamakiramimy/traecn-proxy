using System.Text.Json;
using System.Text.Json.Nodes;

namespace TrancnProxy;

public class TraeAuthData
{
    public string Token { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string UserId { get; set; } = "";
    public DateTimeOffset? ExpiredAt { get; set; }
    public DateTimeOffset? RefreshExpiredAt { get; set; }
    public DateTimeOffset? TokenReleaseAt { get; set; }
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? Region { get; set; }
    public string? ApiHost { get; set; }
    public string? ConsoleHost { get; set; }
    /// <summary>true = 网页授权独立会话,不回写 IDE 的 storage.json</summary>
    public bool Standalone { get; set; }
}

/// <summary>
/// 读取/解密 Trae CN 本地认证数据(storage.json),并提供本地缓存与回写。
/// </summary>
public static class TraeAuthStore
{
    public static string DataDir => Environment.GetEnvironmentVariable("APPDATA") is { } appdata
        ? Path.Combine(appdata, "Trae CN", "User")
        : OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Trae CN", "User")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "Trae CN", "User");

    public static string StoragePath => Path.Combine(DataDir, "globalStorage", "storage.json");
    public static string LocalEnvPath => OperatingSystem.IsMacOS()
        ? Path.Combine(DataDir, "..", "ModularData", "ckg_server", "local_env.json")
        : Path.Combine(DataDir, "ModularData", "ckg_server", "local_env.json");

    public static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "trancn-proxy");
    public static string CachePath => Path.Combine(CacheDir, "auth.json");

    /// <summary>从 storage.json 解密认证数据,失败抛异常。</summary>
    public static TraeAuthData ReadFromStorage()
    {
        if (!File.Exists(StoragePath))
            throw new FileNotFoundException($"未找到 Trae CN 登录数据: {StoragePath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(StoragePath));
        var root = doc.RootElement;

        if (!root.TryGetProperty("iCubeAuthInfo://icube.cloudide", out var encProp))
            throw new InvalidDataException("storage.json 中缺少 iCubeAuthInfo://icube.cloudide 键");

        string enc = encProp.GetString() ?? "";
        string plain;
        if (enc.TrimStart().StartsWith('{'))
            plain = enc; // 国际版明文 JSON
        else
            plain = TcCrypto.DecryptStorageValue(enc);

        var auth = new TraeAuthData();
        using var authDoc = JsonDocument.Parse(plain);
        var a = authDoc.RootElement;
        auth.Token = GetStr(a, "token");
        auth.RefreshToken = GetStr(a, "refreshToken");
        auth.UserId = GetStr(a, "userId");
        auth.ExpiredAt = GetDate(a, "expiredAt");
        auth.RefreshExpiredAt = GetDate(a, "refreshExpiredAt");
        auth.TokenReleaseAt = GetDate(a, "tokenReleaseAt");
        if (a.TryGetProperty("account", out var acct))
        {
            auth.Username = GetStr(acct, "username");
            auth.Email = GetStr(acct, "email");
        }
        if (a.TryGetProperty("userRegion", out var region))
            auth.Region = GetStr(region, "region");

        ReadHostInfo(root, auth);
        return auth;
    }

    public static (string deviceId, string machineId) ReadDeviceIds()
    {
        string deviceId = "0", machineId = "0";
        try
        {
            if (File.Exists(StoragePath))
                using (var doc = JsonDocument.Parse(File.ReadAllText(StoragePath)))
                    if (doc.RootElement.TryGetProperty("telemetry.machineId", out var m) && m.ValueKind == JsonValueKind.String)
                        machineId = m.GetString() ?? machineId;
        }
        catch { }
        try
        {
            if (File.Exists(LocalEnvPath))
                using (var doc = JsonDocument.Parse(File.ReadAllText(LocalEnvPath)))
                    if (doc.RootElement.TryGetProperty("device_id", out var d) && d.ValueKind == JsonValueKind.String)
                        deviceId = d.GetString() ?? deviceId;
        }
        catch { }
        return (deviceId, machineId);
    }

    /// <summary>从缓存读取(可能返回 null)。</summary>
    public static TraeAuthData? ReadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            return JsonSerializer.Deserialize<TraeAuthData>(File.ReadAllText(CachePath));
        }
        catch { return null; }
    }

    public static void SaveCache(TraeAuthData auth)
    {
        Directory.CreateDirectory(CacheDir);
        File.WriteAllText(CachePath, JsonSerializer.Serialize(auth, new JsonSerializerOptions { WriteIndented = true }));
        if (!OperatingSystem.IsWindows()) { try { File.SetUnixFileMode(CachePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { } }
    }

    /// <summary>把刷新后的 token 回写 storage.json(tc 加密),让 IDE 也能继续使用。</summary>
    public static void WriteBackToStorage(TraeAuthData auth)
    {
        if (!File.Exists(StoragePath)) return;
        string raw = File.ReadAllText(StoragePath);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("iCubeAuthInfo://icube.cloudide", out var encProp)) return;

        string enc = encProp.GetString() ?? "";
        string plain = enc.TrimStart().StartsWith('{') ? enc : TcCrypto.DecryptStorageValue(enc);
        var node = JsonNode.Parse(plain)!.AsObject();
        node["token"] = auth.Token;
        node["refreshToken"] = auth.RefreshToken;
        node["expiredAt"] = auth.ExpiredAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        node["refreshExpiredAt"] = auth.RefreshExpiredAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

        string newEnc = enc.TrimStart().StartsWith('{') ? node.ToJsonString() : TcCrypto.EncryptStorageValue(node.ToJsonString());
        string newRaw = raw.Replace(encProp.GetRawText(), JsonSerializer.Serialize(newEnc));
        File.Copy(StoragePath, StoragePath + ".bak", overwrite: true);
        File.WriteAllText(StoragePath, newRaw);
    }

    private static void ReadHostInfo(JsonElement root, TraeAuthData auth)
    {
        try
        {
            if (root.TryGetProperty("iCubeHostInfo", out var hi))
            {
                auth.ConsoleHost = GetStr(hi, "consoleHost");
                auth.ApiHost = GetStr(hi, "apiHost");
            }
        }
        catch { }
        // 租户 host 映射(企业版): {userId: host}
        try
        {
            if (string.IsNullOrEmpty(auth.ApiHost) && File.Exists(LocalEnvPath))
                using (var doc = JsonDocument.Parse(File.ReadAllText(LocalEnvPath)))
                    if (doc.RootElement.TryGetProperty("host_map", out var map) && map.ValueKind == JsonValueKind.Object)
                        foreach (var p in map.EnumerateObject())
                            if (p.Name == auth.UserId && p.Value.ValueKind == JsonValueKind.String)
                                auth.ApiHost = p.Value.GetString();
        }
        catch { }
    }

    private static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTimeOffset? GetDate(JsonElement e, string name)
    {
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(v.GetString(), out var d)) return d;
        return null;
    }
}
