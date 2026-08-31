using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace TrancnProxy.Tests;

[TestClass]
public sealed class MultiAccountTests
{
    private string _dataDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"trancn-proxy-tests-{Guid.NewGuid():N}");
        new TraeAccountStore(_dataDirectory).Save(new MultiAccountSettings());
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }

    [TestMethod]
    public void NewSettingsAndAccounts_DefaultToTenConcurrentRequests()
    {
        var settings = new MultiAccountSettings();
        var account = new TraeAccount();

        settings.DefaultMaxConcurrency.Should().Be(10);
        account.MaxConcurrency.Should().Be(10);
    }

    [TestMethod]
    public void SetMaxConcurrency_AppliesImmediatelyAndPersists()
    {
        var store = new TraeAccountStore(_dataDirectory);
        var manager = new MultiAccountManager(store);
        manager.AddOrReplace(CreateAccount(maxConcurrency: 2));
        using var firstLease = manager.Acquire(null);
        using var secondLease = manager.Acquire(null);

        Action acquireAtLimit = () => manager.Acquire(null);
        acquireAtLimit.Should().Throw<InvalidOperationException>().WithMessage("*并发上限*");

        manager.SetMaxConcurrency("test", 3).Should().BeTrue();
        using var thirdLease = manager.Acquire(null);

        var reloaded = new MultiAccountManager(store);
        reloaded.Accounts.Single().MaxConcurrency.Should().Be(3);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(101)]
    public void SetMaxConcurrency_RejectsValuesOutsideSupportedRange(int maxConcurrency)
    {
        var manager = new MultiAccountManager(new TraeAccountStore(_dataDirectory));
        manager.AddOrReplace(CreateAccount());

        Action update = () => manager.SetMaxConcurrency("test", maxConcurrency);

        update.Should().Throw<InvalidOperationException>().WithMessage("*1 到 100*");
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(100)]
    public void SetMaxConcurrency_AcceptsSupportedBoundaryValues(int maxConcurrency)
    {
        var manager = new MultiAccountManager(new TraeAccountStore(_dataDirectory));
        manager.AddOrReplace(CreateAccount());

        manager.SetMaxConcurrency("test", maxConcurrency).Should().BeTrue();
        manager.Accounts.Single().MaxConcurrency.Should().Be(maxConcurrency);
    }

    [TestMethod]
    public void UpdateSettings_PersistsDefaultMaxConcurrency()
    {
        var store = new TraeAccountStore(_dataDirectory);
        var manager = new MultiAccountManager(store);

        manager.UpdateSettings("balanced", 30, 24);

        var reloaded = new MultiAccountManager(store);
        reloaded.Settings.LoadBalancing.Should().Be("balanced");
        reloaded.Settings.SessionTtlMinutes.Should().Be(30);
        reloaded.Settings.DefaultMaxConcurrency.Should().Be(24);
    }

    [TestMethod]
    public void ImportJson_RejectsUnsupportedMaxConcurrency()
    {
        var manager = new MultiAccountManager(new TraeAccountStore(_dataDirectory));
        const string json = """
            [{"Alias":"invalid","MaxConcurrency":0,"Auth":{"Token":"test-token"}}]
            """;

        Action import = () => manager.ImportJson(json);

        import.Should().Throw<InvalidDataException>().WithMessage("*1 到 100*");
    }

    [TestMethod]
    public void OAuthLogin_RejectsUnsupportedMaxConcurrencyBeforeAuthorization()
    {
        var loginManager = new TraeOAuthLoginManager();

        Action begin = () => loginManager.Begin("test", "http://127.0.0.1/callback", "0", "0", 101);

        begin.Should().Throw<InvalidOperationException>().WithMessage("*1 到 100*");
    }

    private static TraeAccount CreateAccount(int maxConcurrency = 10) => new()
    {
        Alias = "test",
        MaxConcurrency = maxConcurrency,
        Auth = new TraeAuthData { Token = "test-token" }
    };
}