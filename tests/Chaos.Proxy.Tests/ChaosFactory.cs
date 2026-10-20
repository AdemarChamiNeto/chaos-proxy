using System.Net.Http.Headers;
using Chaos.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chaos.Proxy.Tests;

/// <summary>Sorteio fixo: com 0, toda regra com probabilidade &gt; 0 aplica.</summary>
internal sealed class FixedRandom(double value) : IRandomSource
{
    public double NextDouble() => value;
    public int Next(int min, int max) => min;
}

/// <summary>O proxy inteiro em memória, com configuração por teste.</summary>
public sealed class ChaosFactory(Dictionary<string, string?>? settings = null, double random = 0) : WebApplicationFactory<Program>
{
    public const string AdminToken = "token-de-teste-com-mais-de-32-caracteres";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("CHAOS_ADMIN_TOKEN", AdminToken);
        foreach (var (k, v) in settings ?? []) builder.UseSetting(k, v);
        builder.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IRandomSource>(new FixedRandom(random))));
    }

    public HttpClient Admin()
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return c;
    }

    public HttpClient Visitor() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
}

/// <summary>
/// Uma "API real" numa porta de verdade, para testar o repasse pelo YARP. Devolve o que recebeu
/// (método, caminho, query e cabeçalhos) em JSON.
/// </summary>
public sealed class EchoApi : IAsyncDisposable
{
    private readonly WebApplication _app;

    private EchoApi(WebApplication app) => _app = app;

    public string Url => _app.Urls.First();

    public static async Task<EchoApi> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Map("/{**path}", (HttpContext ctx) => Results.Json(new
        {
            method = ctx.Request.Method,
            path = ctx.Request.Path.Value,
            query = ctx.Request.QueryString.Value,
            headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
        }));
        await app.StartAsync();
        return new EchoApi(app);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
