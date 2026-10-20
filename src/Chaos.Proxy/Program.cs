using Chaos.Domain;
using Chaos.Proxy;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// O app não sobe se faltar variável ou se ela estiver errada (a mensagem diz qual, nunca o valor).
var settings = ChaosSettings.Load(builder.Configuration, builder.Environment);

if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1024 * 1024);

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IRandomSource, SystemRandom>();
builder.Services.AddSingleton(sp => new WindowCounter(sp.GetRequiredService<IClock>()));
builder.Services.AddSingleton<ChaosEngine>();
builder.Services.AddSingleton<RuleStore>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<RequestLog>();
builder.Services.AddSingleton<AdminAuth>();
builder.Services.AddSingleton<ChaosHandler>();
builder.Services.AddHttpForwarder();
if (settings.FakeUpstream) builder.Services.AddSingleton<IUpstream, FakeUpstream>();
else builder.Services.AddSingleton<IUpstream, YarpUpstream>();
builder.Services.AddChaosRateLimits(settings);
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "Chaos Proxy";
    doc.Info.Description = "Proxy que injeta falhas entre um cliente e uma API. Tudo fora de /_chaos é repassado à API.";
    return Task.CompletedTask;
}));

var app = builder.Build();

// No Render (e atrás de qualquer balanceador) o IP do cliente vem no X-Forwarded-For.
// Só confia nele com TRUST_FORWARDED_HEADERS=true: na máquina local, qualquer um poderia forjar.
if (settings.TrustForwardedHeaders)
{
    var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
    fwd.KnownIPNetworks.Clear();
    fwd.KnownProxies.Clear();
    app.UseForwardedHeaders(fwd);
}

// Cabeçalhos de segurança só nas páginas e rotas do próprio proxy; o que vem da API passa intacto.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/_chaos"))
    {
        var h = ctx.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    }
    await next();
});

app.UseRateLimiter();

app.MapOpenApi("/_chaos/openapi.json");
app.MapGet("/_chaos", (IWebHostEnvironment env) => Results.File(Path.Combine(env.WebRootPath, "index.html"), "text/html; charset=utf-8"))
    .ExcludeFromDescription()
    .RequireRateLimiting(RateLimits.Read);

// Arquivos da demo por endpoint (e não UseStaticFiles): a rota pega-tudo do proxy casaria antes.
// Lista fechada de nomes: nada de caminho vindo da URL chegar ao disco.
var assets = new Dictionary<string, string>
{
    ["demo.css"] = "text/css; charset=utf-8",
    ["demo.js"] = "text/javascript; charset=utf-8",
    ["icon.svg"] = "image/svg+xml",
};
app.MapGet("/_chaos/static/{file}", (string file, IWebHostEnvironment env) =>
        assets.TryGetValue(file, out var type) ? Results.File(Path.Combine(env.WebRootPath, file), type) : Results.NotFound())
    .ExcludeFromDescription()
    .RequireRateLimiting(RateLimits.Read);
app.MapChaosApi();

// No modo demonstração, a raiz abre a página da demo; com uma API real, a raiz é dela.
if (settings.FakeUpstream)
    app.MapGet("/", () => Results.Redirect("/_chaos")).ExcludeFromDescription();

// Todo o resto vai para a API, passando pelo caos.
app.Map("/{**path}", (HttpContext ctx, ChaosHandler handler) => handler.HandleAsync(ctx))
    .ExcludeFromDescription()
    .RequireRateLimiting(RateLimits.Proxy);

app.Run();

/// <summary>Visível para os testes (WebApplicationFactory).</summary>
public partial class Program;
