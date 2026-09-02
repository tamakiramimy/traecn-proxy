using System.Text.Json;

namespace TrancnProxy;

public sealed class ProxySettings
{
    public ServerSettings Server { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public AccountSettings Accounts { get; set; } = new();
    public UpstreamSettings Upstream { get; set; } = new();
    public IdeBridgeSettings IdeBridge { get; set; } = new();

    public static ProxySettings Load()
    {
        string baseDirectoryPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        string workingDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        string path = File.Exists(baseDirectoryPath) ? baseDirectoryPath : workingDirectoryPath;
        if (!File.Exists(path)) return new ProxySettings();

        try
        {
            return JsonSerializer.Deserialize<ProxySettings>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new ProxySettings();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"appsettings.json 格式无效: {ex.Message}", ex);
        }
    }

    public sealed class ServerSettings
    {
        public int Port { get; set; } = 9220;
        public string Listen { get; set; } = "127.0.0.1";
        public string? PublicBaseUrl { get; set; }
    }

    public sealed class SecuritySettings
    {
        public string? ApiKey { get; set; }
        public string? AdminKey { get; set; }
    }

    public sealed class AccountSettings
    {
        public string? DataDirectory { get; set; }
    }

    public sealed class UpstreamSettings
    {
        public string? ChatApiHost { get; set; }

        /// <summary>新账号未显式声明类型时的默认服务面：auto / enterprise / solo。</summary>
        public string? DefaultAccountKind { get; set; }

        public ClientProfileSettings Enterprise { get; set; } = new();
        public ClientProfileSettings Solo { get; set; } = new();

        /// <summary>
        /// 人工核验过的“请求模型 -> 上游实际模型名”白名单。
        /// 上游目录不声明真实后端模型名，默认的包含判定对部分 config 结构上就无法成立；
        /// 在这里显式登记才放行，避免悳悳放宽降级拦截。
        /// </summary>
        public Dictionary<string, string[]> ModelAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        internal TraeUpstreamOptions ToOptions(string? chatApiHost) => new(
            chatApiHost,
            Enterprise.ToOverrides(),
            Solo.ToOverrides(),
            ModelAliases);
    }

    /// <summary>客户端画像覆盖项，留空表示沿用内置默认值。</summary>
    public sealed class ClientProfileSettings
    {
        public string? IdeVersion { get; set; }
        public string? IdeVersionCode { get; set; }
        public string? DeviceType { get; set; }
        public string? OsVersion { get; set; }
        public string? DeviceBrand { get; set; }

        internal TraeClientProfileOverrides? ToOverrides() =>
            IdeVersion is null && IdeVersionCode is null && DeviceType is null && OsVersion is null && DeviceBrand is null
                ? null
                : new TraeClientProfileOverrides(IdeVersion, IdeVersionCode, DeviceType, OsVersion, DeviceBrand);
    }

    public sealed class IdeBridgeSettings
    {
        public bool Enabled { get; set; } = true;
        public string DebugEndpoint { get; set; } = "http://127.0.0.1:9333";
        public int RequestTimeoutSeconds { get; set; } = 300;
        public int PollIntervalMilliseconds { get; set; } = 35;
    }
}