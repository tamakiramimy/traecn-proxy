using System.Text.Json.Nodes;

namespace TrancnProxy;

/// <summary>TRAE 账号所属的服务面类型。</summary>
public enum TraeAccountKind
{
    /// <summary>按上游配置自动推断。</summary>
    Auto,

    /// <summary>企业控制面（chat_v3 通道）。</summary>
    Enterprise,

    /// <summary>独立 SOLO 服务面（solo_work_lite 通道），消费版账号同属此面。</summary>
    Solo
}

/// <summary>一个服务面要求的客户端画像请求头。</summary>
public sealed record TraeClientProfile(
    string IdeVersion,
    string IdeVersionCode,
    string DeviceType,
    string OsVersion,
    string DeviceBrand,
    string? DeviceCpu,
    string? UserAgent,
    bool SendIdeToken);

/// <summary>可由 appsettings.json 覆盖的客户端画像字段，留空表示沿用内置默认值。</summary>
public sealed record TraeClientProfileOverrides(
    string? IdeVersion = null,
    string? IdeVersionCode = null,
    string? DeviceType = null,
    string? OsVersion = null,
    string? DeviceBrand = null)
{
    internal TraeClientProfile ApplyTo(TraeClientProfile profile) => profile with
    {
        IdeVersion = Pick(IdeVersion, profile.IdeVersion),
        IdeVersionCode = Pick(IdeVersionCode, profile.IdeVersionCode),
        DeviceType = Pick(DeviceType, profile.DeviceType),
        OsVersion = Pick(OsVersion, profile.OsVersion),
        DeviceBrand = Pick(DeviceBrand, profile.DeviceBrand),
        UserAgent = profile.UserAgent is null ? null : $"Trae/{Pick(IdeVersion, profile.IdeVersion)}"
    };

    private static string Pick(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}

/// <summary>构造 <see cref="TraeClient"/> 所需的上游选项。</summary>
public sealed record TraeUpstreamOptions(
    string? ChatApiHost = null,
    TraeClientProfileOverrides? EnterpriseProfile = null,
    TraeClientProfileOverrides? SoloProfile = null);

/// <summary>
/// 描述一个服务面上互相绑定的协议决策：chat 通道、模型目录端点与解析器、客户端画像、默认模型。
/// </summary>
public sealed record TraeServiceFace(
    TraeAccountKind Kind,
    string ChatFunction,
    string CatalogPath,
    bool CatalogOnChatHost,
    bool CatalogAcceptsEventStream,
    string DefaultModelId,
    Func<JsonObject> BuildCatalogRequest,
    Func<JsonNode?, DateTimeOffset?, TraeModelCatalogSnapshot> ParseCatalog,
    TraeClientProfile Profile)
{
    /// <summary>企业控制面画像。TRAE IDE 桌面端形态。</summary>
    public static TraeClientProfile DefaultEnterpriseProfile => new(
        IdeVersion: "3.3.87",
        IdeVersionCode: "20260806",
        DeviceType: OperatingSystem.IsMacOS() ? "mac" : OperatingSystem.IsWindows() ? "windows" : "linux",
        OsVersion: LocalOsVersion(),
        DeviceBrand: LocalDeviceBrand(),
        DeviceCpu: OperatingSystem.IsMacOS() ? "Apple" : "Unknown",
        UserAgent: null,
        SendIdeToken: false);

    /// <summary>独立 SOLO 服务面画像。该面只接受 SOLO 客户端形态，与本机实际环境无关。</summary>
    public static TraeClientProfile DefaultSoloProfile => new(
        IdeVersion: "0.1.43",
        IdeVersionCode: "20260716",
        DeviceType: "windows",
        OsVersion: "Windows 11 Pro",
        DeviceBrand: "83DG",
        DeviceCpu: null,
        UserAgent: "Trae/0.1.43",
        SendIdeToken: true);

    /// <summary>按账号类型选出服务面，<see cref="TraeAccountKind.Auto"/> 时按是否配置独立 chat 服务面推断。</summary>
    /// <param name="kind">账号声明的类型。</param>
    /// <param name="hasStandaloneChatHost">chat 请求是否指向独立服务面主机。</param>
    /// <param name="options">客户端画像覆盖项。</param>
    /// <returns>该账号应使用的服务面。</returns>
    public static TraeServiceFace Resolve(
        TraeAccountKind kind,
        bool hasStandaloneChatHost,
        TraeUpstreamOptions? options = null)
    {
        var effective = kind switch
        {
            TraeAccountKind.Enterprise => TraeAccountKind.Enterprise,
            TraeAccountKind.Solo => TraeAccountKind.Solo,
            _ => hasStandaloneChatHost ? TraeAccountKind.Solo : TraeAccountKind.Enterprise
        };

        return effective == TraeAccountKind.Solo
            ? Solo(options?.SoloProfile)
            : Enterprise(options?.EnterpriseProfile);
    }

    /// <summary>企业控制面：chat_v3 通道，模型 ID 与 config_name 分离。</summary>
    public static TraeServiceFace Enterprise(TraeClientProfileOverrides? profileOverrides = null) => new(
        Kind: TraeAccountKind.Enterprise,
        ChatFunction: "chat_v3",
        CatalogPath: "/api/ide/v1/batch_get_detail_param",
        CatalogOnChatHost: false,
        CatalogAcceptsEventStream: true,
        DefaultModelId: "Doubao-Seed-Evolving__dev",
        BuildCatalogRequest: () => new JsonObject
        {
            ["functions"] = new JsonArray("chat_v3", "chat", "inline_chat"),
            ["agentType"] = "",
            ["currentConfigInfo"] = new JsonObject { ["configName"] = "", ["isCustomModel"] = false },
            ["modeType"] = "Manual",
            ["accessType"] = "Default",
            ["abForceVids"] = "",
            ["abAutotestAdvancedMode"] = 0,
            ["showCustomModel"] = true
        },
        ParseCatalog: TraeModelCatalogParser.Parse,
        Profile: Apply(profileOverrides, DefaultEnterpriseProfile));

    /// <summary>独立 SOLO 服务面：solo_work_lite 通道，模型 ID 即 config_name。</summary>
    public static TraeServiceFace Solo(TraeClientProfileOverrides? profileOverrides = null) => new(
        Kind: TraeAccountKind.Solo,
        ChatFunction: "solo_work_lite",
        CatalogPath: "/api/ide/v1/get_detail_param",
        CatalogOnChatHost: true,
        CatalogAcceptsEventStream: false,
        // 该面按 config_name 选模型，带 __dev 后缀的企业 ID 在这里解析不到。
        DefaultModelId: "Doubao-Seed-Evolving",
        BuildCatalogRequest: () => new JsonObject
        {
            ["function"] = "solo_work_lite",
            ["config_names"] = null,
            ["need_prompt"] = false,
            ["current_config_info"] = null,
            ["poly_prompt"] = true,
            ["mode_type"] = null,
            ["agent_type"] = null
        },
        ParseCatalog: TraeModelCatalogParser.ParseChatConfigs,
        Profile: Apply(profileOverrides, DefaultSoloProfile));

    private static TraeClientProfile Apply(TraeClientProfileOverrides? overrides, TraeClientProfile profile) =>
        overrides?.ApplyTo(profile) ?? profile;

    private static string LocalOsVersion() => $"{Environment.OSVersion.Platform} {Environment.OSVersion.Version}";

    private static string LocalDeviceBrand()
    {
        if (!OperatingSystem.IsMacOS()) return OperatingSystem.IsWindows() ? "PC" : "Linux";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sysctl", "-n hw.model") { RedirectStandardOutput = true };
            using var process = System.Diagnostics.Process.Start(psi);
            string brand = process?.StandardOutput.ReadToEnd().Trim() ?? "";
            return string.IsNullOrEmpty(brand) ? "Mac" : brand;
        }
        catch { return "Mac"; }
    }
}
