using System.Text.Json;

namespace TrancnProxy;

public sealed class ProxySettings
{
    public ServerSettings Server { get; set; } = new();
    public SecuritySettings Security { get; set; } = new();
    public AccountSettings Accounts { get; set; } = new();

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
}