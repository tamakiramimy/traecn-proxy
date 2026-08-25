namespace TrancnProxy;

/// <summary>
/// 后台定时刷新:每 30 分钟检查一次,若 access token 将在 1 小时内过期则用 refreshToken 换新,
/// 并保存到缓存 + 回写 storage.json(让 IDE 与代理共用同一份 token)。
/// </summary>
public class TokenRefreshService
{
    private readonly TraeAuthData _auth;
    private readonly TraeClient _client;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public TokenRefreshService(TraeAuthData auth, TraeClient client)
    {
        _auth = auth;
        _client = client;
    }

    public Task StartAsync(CancellationToken ct) => Task.Run(() => LoopAsync(ct), ct);

    private async Task LoopAsync(CancellationToken ct)
    {
        Console.WriteLine("[refresh] 定时刷新已启动(每 30 分钟检查,过期前 1 小时自动续期)");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (NeedsRefresh())
                    await RefreshOnceAsync(ct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[refresh] 刷新失败: {ex.Message}");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(30), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public bool NeedsRefresh()
    {
        if (_auth.ExpiredAt is null) return false;
        return DateTimeOffset.UtcNow >= _auth.ExpiredAt.Value.AddHours(-1);
    }

    public async Task<bool> RefreshOnceAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            if (_auth.RefreshExpiredAt is not null && DateTimeOffset.UtcNow > _auth.RefreshExpiredAt)
            {
                Console.WriteLine($"[refresh] refreshToken 已过期({_auth.RefreshExpiredAt}),需要重新登录");
                return false;
            }
            Console.WriteLine("[refresh] 正在续期 ...");
            await _client.ExchangeTokenAsync(ct: ct);
            _auth.TokenReleaseAt = DateTimeOffset.UtcNow;
            TraeAuthStore.SaveCache(_auth);
            if (!_auth.Standalone)
            {
                try { TraeAuthStore.WriteBackToStorage(_auth); Console.WriteLine("[refresh] 已回写 storage.json"); }
                catch (Exception ex) { Console.WriteLine($"[refresh] 回写失败: {ex.Message}"); }
            }
            Console.WriteLine($"[refresh] 续期成功,新过期时间: {_auth.ExpiredAt:yyyy-MM-dd HH:mm}Z");
            return true;
        }
        finally
        {
            _mutex.Release();
        }
    }
}
