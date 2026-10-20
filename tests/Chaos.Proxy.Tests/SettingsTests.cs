using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Chaos.Proxy.Tests;

public class SettingsTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "teste";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static ChaosSettings Load(string env, params (string Key, string Value)[] values) =>
        ChaosSettings.Load(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build(), new Env(env));

    [Fact]
    public void Em_desenvolvimento_sobe_sem_nada_no_modo_demonstracao()
    {
        var s = Load("Development");
        Assert.True(s.FakeUpstream);
        Assert.Null(s.AdminToken);
        Assert.Equal(600, s.ProxyLimitPerMinute);
    }

    [Fact]
    public void Em_producao_exige_token()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Load("Production"));
        Assert.Contains("CHAOS_ADMIN_TOKEN", e.Message);
    }

    [Fact]
    public void Junta_todos_os_problemas_sem_mostrar_os_valores()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Load("Development",
            ("UPSTREAM_URL", "ftp://api"), ("CHAOS_ADMIN_TOKEN", "curto-demais"), ("PROXY_RATE_LIMIT", "0")));
        Assert.Contains("UPSTREAM_URL", e.Message);
        Assert.Contains("32 caracteres", e.Message);
        Assert.Contains("PROXY_RATE_LIMIT", e.Message);
        Assert.DoesNotContain("curto-demais", e.Message);
    }

    [Fact]
    public void Url_da_API_sem_barra_no_fim()
    {
        var s = Load("Production", ("UPSTREAM_URL", "http://api:8080/"), ("CHAOS_ADMIN_TOKEN", new string('x', 40)), ("TRUST_FORWARDED_HEADERS", "true"));
        Assert.Equal("http://api:8080/", s.Upstream!.ToString());
        Assert.True(s.TrustForwardedHeaders);
    }
}
