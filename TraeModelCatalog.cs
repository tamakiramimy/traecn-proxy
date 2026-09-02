using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace TrancnProxy;

/// <summary>Identifies the TRAE entitlement variant encoded in a model ID.</summary>
public enum TraeModelVariant
{
    /// <summary>The model ID has no recognized entitlement suffix.</summary>
    Other,

    /// <summary>The development entitlement variant.</summary>
    Dev,

    /// <summary>The maximum entitlement variant.</summary>
    Max
}

/// <summary>Describes one selectable model returned by the TRAE IDE catalog.</summary>
/// <param name="Id">The exact upstream <c>model_name</c>.</param>
/// <param name="ConfigName">The parent IDE configuration name.</param>
/// <param name="DisplayName">The user-facing model name.</param>
/// <param name="Variant">The entitlement variant encoded in <paramref name="Id"/>.</param>
public sealed record TraeModelDescriptor(
    string Id,
    string ConfigName,
    string DisplayName,
    TraeModelVariant Variant);

/// <summary>An immutable snapshot of selectable models for one TRAE account.</summary>
public sealed class TraeModelCatalogSnapshot
{
    private readonly IReadOnlyDictionary<string, TraeModelDescriptor> _modelsById;

    /// <summary>Initializes a catalog snapshot.</summary>
    /// <param name="models">Selectable models in server order.</param>
    /// <param name="retrievedAt">The time at which the catalog was retrieved.</param>
    /// <param name="skipped">Upstream configs that were filtered out, with the reason.</param>
    public TraeModelCatalogSnapshot(
        IEnumerable<TraeModelDescriptor> models,
        DateTimeOffset retrievedAt,
        IEnumerable<string>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(models);

        var modelList = models.ToArray();
        Models = Array.AsReadOnly(modelList);
        RetrievedAt = retrievedAt;
        Skipped = Array.AsReadOnly((skipped ?? []).ToArray());
        _modelsById = new ReadOnlyDictionary<string, TraeModelDescriptor>(
            modelList.ToDictionary(model => model.Id, StringComparer.Ordinal));
    }

    /// <summary>Gets the selectable models in server order.</summary>
    public IReadOnlyList<TraeModelDescriptor> Models { get; }

    /// <summary>Gets the upstream configs that were filtered out, each with its reason.</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>Gets the time at which this snapshot was retrieved.</summary>
    public DateTimeOffset RetrievedAt { get; }

    /// <summary>Finds a model by its exact upstream ID.</summary>
    /// <param name="modelId">The exact model ID to find.</param>
    /// <param name="model">The matching descriptor when found.</param>
    /// <returns><see langword="true"/> when the model exists.</returns>
    public bool TryGetModel(string modelId, out TraeModelDescriptor? model) =>
        _modelsById.TryGetValue(modelId, out model);
}

/// <summary>Thrown when the TRAE IDE model catalog has an invalid schema.</summary>
public sealed class TraeModelCatalogException(string message) : Exception(message);

/// <summary>Thrown when an exact model ID is absent from the refreshed TRAE catalog.</summary>
public sealed class TraeModelNotFoundException(string modelId)
    : Exception($"Model '{modelId}' is not available in the current TRAE account catalog.")
{
    /// <summary>Gets the unavailable model ID.</summary>
    public string ModelId { get; } = modelId;
}

/// <summary>Maintains a single-flight, time-limited model catalog snapshot.</summary>
public sealed class TraeModelCatalogCache
{
    private static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(5);
    private readonly Func<CancellationToken, Task<JsonNode>> _loadCatalog;
    private readonly Func<JsonNode?, DateTimeOffset?, TraeModelCatalogSnapshot> _parseCatalog;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _timeToLive;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private TraeModelCatalogSnapshot? _snapshot;

    /// <summary>Initializes a model catalog cache.</summary>
    /// <param name="loadCatalog">Loads one raw catalog response.</param>
    /// <param name="timeProvider">Provides time for expiration checks.</param>
    /// <param name="timeToLive">Controls snapshot lifetime.</param>
    /// <param name="parseCatalog">Parses one raw catalog response.</param>
    public TraeModelCatalogCache(
        Func<CancellationToken, Task<JsonNode>> loadCatalog,
        TimeProvider? timeProvider = null,
        TimeSpan? timeToLive = null,
        Func<JsonNode?, DateTimeOffset?, TraeModelCatalogSnapshot>? parseCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(loadCatalog);
        if (timeToLive <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeToLive), "Catalog TTL must be greater than zero.");

        _loadCatalog = loadCatalog;
        _parseCatalog = parseCatalog ?? TraeModelCatalogParser.Parse;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timeToLive = timeToLive ?? DefaultTimeToLive;
    }

    /// <summary>Gets a current snapshot, refreshing it when expired or forced.</summary>
    /// <param name="force">Forces an upstream refresh.</param>
    /// <param name="cancellationToken">Cancels waiting or loading.</param>
    /// <returns>The current catalog snapshot.</returns>
    public async Task<TraeModelCatalogSnapshot> GetAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (!force && IsCurrent(snapshot)) return snapshot!;
        var snapshotBeforeWait = snapshot;

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = Volatile.Read(ref _snapshot);
            if (!force && IsCurrent(snapshot)) return snapshot!;
            if (force && snapshot is not null && !ReferenceEquals(snapshot, snapshotBeforeWait)) return snapshot;

            JsonNode rawCatalog = await _loadCatalog(cancellationToken).ConfigureAwait(false);
            var refreshed = _parseCatalog(rawCatalog, _timeProvider.GetUtcNow());
            Volatile.Write(ref _snapshot, refreshed);
            return refreshed;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>Resolves an exact model ID, forcing one refresh after a miss.</summary>
    /// <param name="modelId">The exact upstream model ID.</param>
    /// <param name="cancellationToken">Cancels catalog loading.</param>
    /// <returns>The matching model descriptor.</returns>
    /// <exception cref="TraeModelNotFoundException">The refreshed catalog does not contain the model.</exception>
    public async Task<TraeModelDescriptor> ResolveAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var snapshot = await GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (snapshot.TryGetModel(modelId, out var model)) return model!;

        snapshot = await GetAsync(force: true, cancellationToken).ConfigureAwait(false);
        if (snapshot.TryGetModel(modelId, out model)) return model!;
        throw new TraeModelNotFoundException(modelId);
    }

    private bool IsCurrent(TraeModelCatalogSnapshot? snapshot) =>
        snapshot is not null && _timeProvider.GetUtcNow() - snapshot.RetrievedAt < _timeToLive;
}

/// <summary>Parses the TRAE IDE <c>chat_v3</c> model catalog.</summary>
public static class TraeModelCatalogParser
{
    /// <summary>Parses a catalog response into an immutable snapshot.</summary>
    /// <param name="catalog">The raw catalog response.</param>
    /// <param name="retrievedAt">The retrieval time, or UTC now when omitted.</param>
    /// <returns>The selectable model snapshot.</returns>
    /// <exception cref="TraeModelCatalogException">The response schema is invalid.</exception>
    public static TraeModelCatalogSnapshot Parse(JsonNode? catalog, DateTimeOffset? retrievedAt = null)
    {
        if (catalog is not JsonObject root)
            throw new TraeModelCatalogException("Model catalog root must be an object.");

        var functionConfigs = RequiredArray(root, "function_configs", "catalog");
        var chatConfigs = functionConfigs
            .OfType<JsonObject>()
            .Where(config => StringValue(config["function"]) == "chat_v3")
            .ToArray();
        if (chatConfigs.Length != 1)
            throw new TraeModelCatalogException($"Model catalog must contain exactly one chat_v3 function, found {chatConfigs.Length}.");

        var descriptors = new List<TraeModelDescriptor>();
        var descriptorsById = new Dictionary<string, TraeModelDescriptor>(StringComparer.Ordinal);
        var skipped = new List<string>();
        var configInfos = RequiredArray(chatConfigs[0], "config_info_list", "chat_v3");
        foreach (var configNode in configInfos)
        {
            if (configNode is not JsonObject config)
                throw new TraeModelCatalogException("chat_v3.config_info_list must contain objects.");

            string configName = RequiredString(config, "config_name", "chat_v3 config");
            if (!RequiredBoolean(config, "config_switch", configName))
            {
                skipped.Add($"{configName} (config_switch=false)");
                continue;
            }
            if (OptionalBoolean(config, "is_invisible_to_user", configName) == true)
            {
                skipped.Add($"{configName} (is_invisible_to_user=true)");
                continue;
            }

            string displayName = StringValue(config["display_config"]?["display_name"]);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                skipped.Add($"{configName} (no display_name)");
                continue;
            }

            var modelDetails = RequiredArray(config, "model_detail_list", configName);
            foreach (var detailNode in modelDetails)
            {
                if (detailNode is not JsonObject detail)
                    throw new TraeModelCatalogException($"{configName}.model_detail_list must contain objects.");

                string modelId = RequiredString(detail, "model_name", configName);
                var descriptor = new TraeModelDescriptor(modelId, configName, displayName, GetVariant(modelId));
                if (descriptorsById.TryGetValue(modelId, out var existing))
                {
                    if (existing != descriptor)
                        throw new TraeModelCatalogException($"Model ID '{modelId}' maps to conflicting configurations.");
                    continue;
                }

                descriptorsById.Add(modelId, descriptor);
                descriptors.Add(descriptor);
            }
        }

        if (descriptors.Count == 0)
            throw new TraeModelCatalogException("Model catalog contains no selectable chat_v3 models.");

        return new TraeModelCatalogSnapshot(descriptors, retrievedAt ?? DateTimeOffset.UtcNow, skipped);
    }

    private static JsonArray RequiredArray(JsonObject owner, string propertyName, string ownerName) =>
        owner[propertyName] as JsonArray
        ?? throw new TraeModelCatalogException($"{ownerName}.{propertyName} must be an array.");
    private static string RequiredString(JsonObject owner, string propertyName, string ownerName)
    {
        string value = StringValue(owner[propertyName]);
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new TraeModelCatalogException($"{ownerName}.{propertyName} must be a non-empty string.");
    }

    private static bool RequiredBoolean(JsonObject owner, string propertyName, string ownerName) =>
        owner[propertyName] is JsonValue value && value.TryGetValue<bool>(out bool result)
            ? result
            : throw new TraeModelCatalogException($"{ownerName}.{propertyName} must be a boolean.");

    private static bool? OptionalBoolean(JsonObject owner, string propertyName, string ownerName)
    {
        if (owner[propertyName] is null) return null;
        return owner[propertyName] is JsonValue value && value.TryGetValue<bool>(out bool result)
            ? result
            : throw new TraeModelCatalogException($"{ownerName}.{propertyName} must be a boolean when present.");
    }

    private static string StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out string? result) ? result ?? "" : "";

    private static TraeModelVariant GetVariant(string modelId)
    {
        if (modelId.EndsWith("__dev", StringComparison.Ordinal)) return TraeModelVariant.Dev;
        if (modelId.EndsWith("__max", StringComparison.Ordinal)) return TraeModelVariant.Max;
        return TraeModelVariant.Other;
    }

    /// <summary>Parses the standalone chat service <c>get_detail_param</c> catalog.</summary>
    /// <param name="catalog">The raw catalog response.</param>
    /// <param name="retrievedAt">The retrieval time, or UTC now when omitted.</param>
    /// <returns>The selectable model snapshot keyed by config name.</returns>
    /// <exception cref="TraeModelCatalogException">The response schema is invalid.</exception>
    public static TraeModelCatalogSnapshot ParseChatConfigs(JsonNode? catalog, DateTimeOffset? retrievedAt = null)
    {
        if (catalog is not JsonObject root)
            throw new TraeModelCatalogException("Chat model catalog root must be an object.");

        var configInfos = (root["config_info_list"]
            ?? root["Result"]?["config_info_list"]
            ?? root["data"]?["config_info_list"]) as JsonArray
            ?? throw new TraeModelCatalogException("Chat model catalog has no config_info_list array.");

        var descriptors = new List<TraeModelDescriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var config in configInfos.OfType<JsonObject>())
        {
            string configName = StringValue(config["config_name"]);
            if (string.IsNullOrWhiteSpace(configName) || !seen.Add(configName)) continue;
            if (OptionalBoolean(config, "is_invisible_to_user", configName) == true) continue;

            string displayName = StringValue(config["display_config"]?["display_name"]);
            // 该服务面按 config_name 选模型，model_detail_list 里的 __dev/__max 只是内部变体。
            descriptors.Add(new TraeModelDescriptor(
                configName,
                configName,
                string.IsNullOrWhiteSpace(displayName) ? configName : displayName,
                GetVariant(configName)));
        }

        if (descriptors.Count == 0)
            throw new TraeModelCatalogException("Chat model catalog contains no selectable configurations.");

        return new TraeModelCatalogSnapshot(descriptors, retrievedAt ?? DateTimeOffset.UtcNow);
    }
}