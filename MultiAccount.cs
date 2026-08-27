using System.Collections.Concurrent;
using System.Text.Json;

namespace TrancnProxy;

public sealed class TraeAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Alias { get; set; } = "default";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public int MaxConcurrency { get; set; } = 1;
    public string DeviceId { get; set; } = "0";
    public string MachineId { get; set; } = "0";
    public TraeAuthData Auth { get; set; } = new();
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class MultiAccountSettings
{
    public int Version { get; set; } = 1;
    public string LoadBalancing { get; set; } = "priority";
    public int SessionTtlMinutes { get; set; } = 60;
    public List<TraeAccount> Accounts { get; set; } = new();
}

public sealed class TraeAccountStore
{
    private readonly object _writeGate = new();
    private readonly string _filePath;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public TraeAccountStore(string? dataDirectory = null)
    {
        dataDirectory ??= TraeAuthStore.CacheDir;
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "accounts.json");
    }

    public MultiAccountSettings LoadOrMigrate()
    {
        if (File.Exists(_filePath))
        {
            var settings = JsonSerializer.Deserialize<MultiAccountSettings>(File.ReadAllText(_filePath), JsonOptions);
            return settings ?? new MultiAccountSettings();
        }

        var auth = TraeAuthStore.ReadCache();
        if (auth is null || string.IsNullOrWhiteSpace(auth.Token))
        {
            try { auth = TraeAuthStore.ReadFromStorage(); }
            catch { auth = null; }
        }

        var result = new MultiAccountSettings();
        if (auth is not null && !string.IsNullOrWhiteSpace(auth.Token))
        {
            var (deviceId, machineId) = TraeAuthStore.ReadDeviceIds();
            result.Accounts.Add(new TraeAccount
            {
                Alias = "default",
                Auth = auth,
                DeviceId = deviceId,
                MachineId = machineId
            });
        }
        Save(result);
        return result;
    }

    public void Save(MultiAccountSettings settings)
    {
        lock (_writeGate)
        {
            string temporaryPath = _filePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, _filePath, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                catch { }
            }
        }
    }
}

public sealed class AccountLease : IDisposable
{
    private readonly TraeAccountRuntime _runtime;
    private int _released;

    internal AccountLease(TraeAccountRuntime runtime)
    {
        _runtime = runtime;
        Account = runtime.Account;
        Client = runtime.Client;
    }

    public TraeAccount Account { get; }
    public TraeClient Client { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _runtime.Release();
    }
}

internal sealed class TraeAccountRuntime
{
    private int _inFlight;

    public TraeAccountRuntime(TraeAccount account)
    {
        Account = account;
        Client = new TraeClient(account.Auth, account.DeviceId, account.MachineId);
    }

    public TraeAccount Account { get; }
    public TraeClient Client { get; }
    public SemaphoreSlim RefreshGate { get; } = new(1, 1);
    public int InFlight => Volatile.Read(ref _inFlight);

    public bool TryAcquire()
    {
        while (true)
        {
            int current = Volatile.Read(ref _inFlight);
            if (Account.MaxConcurrency > 0 && current >= Account.MaxConcurrency) return false;
            if (Interlocked.CompareExchange(ref _inFlight, current + 1, current) == current)
            {
                Account.LastUsedAt = DateTimeOffset.UtcNow;
                return true;
            }
        }
    }

    public void Release() => Interlocked.Decrement(ref _inFlight);
}

public sealed class MultiAccountManager
{
    private readonly TraeAccountStore _store;
    private readonly object _selectionGate = new();
    private readonly ConcurrentDictionary<string, TraeAccountRuntime> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SessionBinding> _sessions = new(StringComparer.Ordinal);

    public MultiAccountManager(TraeAccountStore store)
    {
        _store = store;
        Settings = store.LoadOrMigrate();
        foreach (var account in Settings.Accounts)
            _accounts[account.Id] = new TraeAccountRuntime(account);
    }

    public MultiAccountSettings Settings { get; private set; }

    public IReadOnlyCollection<TraeAccount> Accounts => _accounts.Values.Select(x => x.Account).ToArray();

    public TraeAccount AddOrReplace(TraeAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.Alias))
            throw new InvalidOperationException("账号别名不能为空。");

        lock (_selectionGate)
        {
            var duplicate = _accounts.Values.FirstOrDefault(x =>
                x.Account.Alias.Equals(account.Alias, StringComparison.OrdinalIgnoreCase) && x.Account.Id != account.Id);
            if (duplicate is not null)
                throw new InvalidOperationException($"账号别名 '{account.Alias}' 已存在。");

            if (string.IsNullOrWhiteSpace(account.Id)) account.Id = Guid.NewGuid().ToString("N");
            _accounts[account.Id] = new TraeAccountRuntime(account);
            Settings.Accounts = _accounts.Values.Select(x => x.Account).OrderBy(x => x.Alias).ToList();
            _store.Save(Settings);
            return account;
        }
    }

    public bool Remove(string alias)
    {
        lock (_selectionGate)
        {
            var runtime = _accounts.Values.FirstOrDefault(x => x.Account.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
            if (runtime is null) return false;
            _accounts.TryRemove(runtime.Account.Id, out _);
            Settings.Accounts = _accounts.Values.Select(x => x.Account).OrderBy(x => x.Alias).ToList();
            _store.Save(Settings);
            return true;
        }
    }

    public bool SetEnabled(string alias, bool enabled)
    {
        lock (_selectionGate)
        {
            var runtime = FindRuntime(alias);
            if (runtime is null) return false;
            runtime.Account.Enabled = enabled;
            runtime.Account.LastError = enabled ? null : "manually disabled";
            _store.Save(Settings);
            return true;
        }
    }

    public bool SetPriority(string alias, int priority)
    {
        lock (_selectionGate)
        {
            var runtime = FindRuntime(alias);
            if (runtime is null) return false;
            runtime.Account.Priority = priority;
            _store.Save(Settings);
            return true;
        }
    }

    public void UpdateSettings(string loadBalancing, int sessionTtlMinutes)
    {
        if (loadBalancing is not ("priority" or "balanced"))
            throw new InvalidOperationException("负载策略仅支持 priority 或 balanced。");
        if (sessionTtlMinutes is < 1 or > 1440)
            throw new InvalidOperationException("会话 TTL 必须介于 1 到 1440 分钟之间。");
        lock (_selectionGate)
        {
            Settings.LoadBalancing = loadBalancing;
            Settings.SessionTtlMinutes = sessionTtlMinutes;
            _store.Save(Settings);
        }
    }

    public void ImportJson(string json)
    {
        MultiAccountSettings settings;
        try
        {
            JsonElement root = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
            settings = root.ValueKind switch
            {
                JsonValueKind.Array => new MultiAccountSettings
                {
                    Accounts = root.Deserialize<List<TraeAccount>>(JsonOptions) ?? new List<TraeAccount>()
                },
                JsonValueKind.Object => root.Deserialize<MultiAccountSettings>(JsonOptions) ?? new MultiAccountSettings(),
                _ => throw new InvalidDataException("导入内容必须是 accounts.json 对象或账号数组。")
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"导入 JSON 格式无效: {ex.Message}", ex);
        }

        if (settings.Accounts.Count == 0)
            throw new InvalidDataException("导入文件未包含账号。");
        if (settings.Accounts.Any(x => string.IsNullOrWhiteSpace(x.Alias) || string.IsNullOrWhiteSpace(x.Auth.Token)))
            throw new InvalidDataException("每个账号必须包含 alias 和 access token。");
        if (settings.Accounts.GroupBy(x => x.Alias, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new InvalidDataException("导入文件中存在重复账号别名。");
        if (settings.Accounts.Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new InvalidDataException("导入文件中存在重复账号 ID。");
        ValidateSettings(settings.LoadBalancing, settings.SessionTtlMinutes);

        foreach (var account in settings.Accounts)
            if (string.IsNullOrWhiteSpace(account.Id)) account.Id = Guid.NewGuid().ToString("N");
        settings.Accounts = settings.Accounts.OrderBy(x => x.Alias).ToList();
        var runtimes = settings.Accounts.Select(account => new TraeAccountRuntime(account)).ToList();

        lock (_selectionGate)
        {
            // 先完成原子文件写入，持久化失败时保留当前内存账号池。
            _store.Save(settings);
            _accounts.Clear();
            foreach (var runtime in runtimes)
                _accounts[runtime.Account.Id] = runtime;
            Settings = settings;
            _sessions.Clear();
        }
    }

    public AccountLease Acquire(string? sessionKey)
    {
        lock (_selectionGate)
        {
            var candidates = _accounts.Values.Where(IsAvailable).ToList();
            if (candidates.Count == 0)
                throw new InvalidOperationException("没有可用的 Trae 账号，请在管理端添加或启用账号。");

            TraeAccountRuntime? selected = FindStickyRuntime(sessionKey, candidates);
            if (selected is null || !selected.TryAcquire())
            {
                selected = OrderCandidates(candidates)
                    .Where(x => !ReferenceEquals(x, selected))
                    .FirstOrDefault(x => x.TryAcquire())
                    ?? throw new InvalidOperationException("所有 Trae 账号均达到并发上限。");
            }

            if (!string.IsNullOrWhiteSpace(sessionKey))
                _sessions[sessionKey] = new SessionBinding(selected.Account.Id, DateTimeOffset.UtcNow.AddMinutes(Settings.SessionTtlMinutes));
            return new AccountLease(selected);
        }
    }

    public AccountLease AcquireByAlias(string alias)
    {
        lock (_selectionGate)
        {
            var runtime = FindRuntime(alias) ?? throw new InvalidOperationException($"未找到账号 '{alias}'。");
            if (!IsAvailable(runtime)) throw new InvalidOperationException($"账号 '{alias}' 当前不可用。");
            if (!runtime.TryAcquire()) throw new InvalidOperationException($"账号 '{alias}' 已达到并发上限。");
            return new AccountLease(runtime);
        }
    }

    public async Task<bool> RefreshAsync(string alias, CancellationToken ct = default)
    {
        var runtime = FindRuntime(alias) ?? throw new InvalidOperationException($"未找到账号 '{alias}'。");
        await runtime.RefreshGate.WaitAsync(ct);
        try
        {
            if (runtime.Account.Auth.RefreshExpiredAt is not null && runtime.Account.Auth.RefreshExpiredAt <= DateTimeOffset.UtcNow)
            {
                runtime.Account.Enabled = false;
                runtime.Account.LastError = "refresh token expired";
                _store.Save(Settings);
                return false;
            }
            await runtime.Client.ExchangeTokenAsync(ct: ct);
            runtime.Account.Auth.TokenReleaseAt = DateTimeOffset.UtcNow;
            runtime.Account.LastSuccessAt = DateTimeOffset.UtcNow;
            runtime.Account.LastError = null;
            _store.Save(Settings);
            return true;
        }
        catch (Exception ex)
        {
            runtime.Account.LastError = ex.Message;
            _store.Save(Settings);
            return false;
        }
        finally
        {
            runtime.RefreshGate.Release();
        }
    }

    public async Task RefreshExpiringAccountsAsync(CancellationToken ct)
    {
        foreach (var account in Accounts.Where(x => x.Enabled && x.Auth.ExpiredAt is not null && x.Auth.ExpiredAt <= DateTimeOffset.UtcNow.AddHours(1)))
            await RefreshAsync(account.Alias, ct);
    }

    private bool IsAvailable(TraeAccountRuntime runtime) => runtime.Account.Enabled &&
        !string.IsNullOrWhiteSpace(runtime.Account.Auth.Token) &&
        (runtime.Account.Auth.RefreshExpiredAt is null || runtime.Account.Auth.RefreshExpiredAt > DateTimeOffset.UtcNow);

    private TraeAccountRuntime? FindStickyRuntime(string? sessionKey, IReadOnlyCollection<TraeAccountRuntime> candidates)
    {
        if (string.IsNullOrWhiteSpace(sessionKey) || !_sessions.TryGetValue(sessionKey, out var binding)) return null;
        if (binding.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _sessions.TryRemove(sessionKey, out _);
            return null;
        }
        return candidates.FirstOrDefault(x => x.Account.Id == binding.AccountId);
    }

    private IOrderedEnumerable<TraeAccountRuntime> OrderCandidates(IEnumerable<TraeAccountRuntime> candidates) =>
        Settings.LoadBalancing.Equals("balanced", StringComparison.OrdinalIgnoreCase)
            ? candidates.OrderBy(x => x.InFlight).ThenBy(x => x.Account.LastUsedAt ?? DateTimeOffset.MinValue).ThenBy(x => x.Account.Priority)
            : candidates.OrderBy(x => x.Account.Priority).ThenBy(x => x.InFlight).ThenBy(x => x.Account.LastUsedAt ?? DateTimeOffset.MinValue);

    private static void ValidateSettings(string loadBalancing, int sessionTtlMinutes)
    {
        if (loadBalancing is not ("priority" or "balanced"))
            throw new InvalidDataException("负载策略仅支持 priority 或 balanced。");
        if (sessionTtlMinutes is < 1 or > 1440)
            throw new InvalidDataException("会话 TTL 必须介于 1 到 1440 分钟之间。");
    }

    private TraeAccountRuntime? FindRuntime(string alias) => _accounts.Values.FirstOrDefault(x =>
        x.Account.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));

    private sealed record SessionBinding(string AccountId, DateTimeOffset ExpiresAt);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}