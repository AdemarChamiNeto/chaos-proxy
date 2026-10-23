using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Chaos.Proxy.Tests;

public class ProxyTests
{
    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Body(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static string? Fault(HttpResponseMessage res) =>
        res.Headers.TryGetValues("X-Chaos-Fault", out var v) ? v.Single() : null;

    // ---------- modo demonstração ----------

    [Fact]
    public async Task Sem_UPSTREAM_URL_abre_com_a_API_de_demonstracao()
    {
        await using var app = new ChaosFactory();
        var c = app.Visitor();

        var res = await c.GetAsync("/api/tickets?page=2");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Null(Fault(res));
        var body = await Body(res);
        Assert.Equal(2, body.GetProperty("meta").GetProperty("page").GetInt32());
        Assert.Equal(10, body.GetProperty("data").GetArrayLength());

        var raiz = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, raiz.StatusCode);
        Assert.Equal("/_chaos", raiz.Headers.Location!.ToString());

        var health = await Body(await c.GetAsync("/_chaos/health"));
        Assert.Equal("fake", health.GetProperty("upstream").GetString());
    }

    [Fact]
    public async Task Pagina_da_demo_com_cabecalhos_de_seguranca_e_arquivos_so_da_lista()
    {
        await using var app = new ChaosFactory();
        var c = app.Visitor();

        var page = await c.GetAsync("/_chaos");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/_chaos/static/demo.js")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/_chaos/static/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/_chaos/static/..%2F..%2FProgram.cs")).StatusCode);
    }

    // ---------- administração ----------

    [Fact]
    public async Task Administracao_exige_token_e_bloqueia_depois_de_5_erros()
    {
        await using var app = new ChaosFactory();
        // sem token também conta como tentativa errada: 1 sem token + 4 com token errado = 5
        var semToken = app.Visitor();
        Assert.Equal(HttpStatusCode.Unauthorized, (await semToken.GetAsync("/_chaos/rules")).StatusCode);

        var errado = app.Visitor();
        errado.DefaultRequestHeaders.Authorization = new("Bearer", "token-errado");
        for (var i = 0; i < 4; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await errado.GetAsync("/_chaos/rules")).StatusCode);

        // bloqueado: nem o token certo passa até a janela virar (o IP é o mesmo nos testes)
        var bloqueado = await app.Admin().GetAsync("/_chaos/rules");
        Assert.Equal((HttpStatusCode)429, bloqueado.StatusCode);
        Assert.NotNull(bloqueado.Headers.RetryAfter);
    }

    [Fact]
    public async Task Cenario_global_fora_vira_503_marcado_no_formato_da_API()
    {
        await using var app = new ChaosFactory();
        var admin = app.Admin();
        var c = app.Visitor();

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/_chaos/rules/scenario/fora", null)).StatusCode);
        var res = await c.GetAsync("/api/tickets");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("error; rule=fora; status=503", Fault(res));
        Assert.Equal("injected_fault", (await Body(res)).GetProperty("error").GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/_chaos/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsync("/_chaos/rules/scenario/nao-existe", null)).StatusCode);
    }

    [Fact]
    public async Task Regras_invalidas_viram_422_com_o_campo()
    {
        await using var app = new ChaosFactory();
        var res = await app.Admin().PutAsync("/_chaos/rules", Json(new
        {
            rules = new object[]
            {
                new { name = "lenta", kind = "latency", delayMinMs = 5000, delayMaxMs = 100 },
                new { name = "erro", kind = "error", status = 200 },
            },
        }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var details = (await Body(res)).GetProperty("error").GetProperty("details");
        Assert.True(details.TryGetProperty("rules[0].delayMaxMs", out _));
        Assert.True(details.TryGetProperty("rules[1].status", out _));
    }

    [Fact]
    public async Task Corpo_invalido_400_tipo_errado_415_grande_413()
    {
        await using var app = new ChaosFactory();
        var admin = app.Admin();

        var campoAMais = await admin.PutAsync("/_chaos/rules", Json(new { rules = Array.Empty<object>(), extra = 1 }));
        Assert.Equal(HttpStatusCode.BadRequest, campoAMais.StatusCode);

        var tipoDesconhecido = await admin.PutAsync("/_chaos/rules", Json(new { rules = new[] { new { name = "x", kind = "meteoro" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, tipoDesconhecido.StatusCode);

        var texto = await admin.PutAsync("/_chaos/rules", new StringContent("{}", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, texto.StatusCode);

        var grande = await admin.PutAsync("/_chaos/rules", Json(new { rules = Array.Empty<object>(), pad = new string('x', 70_000) }));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, grande.StatusCode);
    }

    [Fact]
    public async Task Regras_personalizadas_com_metodo_e_rota()
    {
        await using var app = new ChaosFactory();
        var ok = await app.Admin().PutAsync("/_chaos/rules", Json(new
        {
            rules = new[] { new { name = "so-post", kind = "error", status = 500, method = "POST", path = "/api/tickets" } },
        }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var c = app.Visitor();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, (await c.PostAsync("/api/tickets", Json(new { title = "x" }))).StatusCode);
    }

    // ---------- sessão da demo ----------

    [Fact]
    public async Task Cenario_da_sessao_vale_so_para_o_visitante()
    {
        await using var app = new ChaosFactory();
        var a = app.Visitor();
        var b = app.Visitor();

        var escolha = await a.PutAsync("/_chaos/session", Json(new { scenario = "fora" }));
        Assert.Equal(HttpStatusCode.OK, escolha.StatusCode);
        var cookie = escolha.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await a.GetAsync("/api/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/tickets")).StatusCode);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await a.PutAsync("/_chaos/session", Json(new { scenario = "inventado" }))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync("/_chaos/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/tickets")).StatusCode);
    }

    [Fact]
    public async Task Log_mostra_ao_visitante_so_o_dele_e_sem_query_string()
    {
        await using var app = new ChaosFactory();
        var a = app.Visitor();
        await a.PutAsync("/_chaos/session", Json(new { scenario = "lenta" }));
        await a.GetAsync("/api/tickets?token=segredo");
        await app.Visitor().GetAsync("/api/tickets/3");

        var meu = await Body(await a.GetAsync("/_chaos/log"));
        var entrada = Assert.Single(meu.EnumerateArray());
        Assert.Equal("/api/tickets", entrada.GetProperty("path").GetString());
        // o id da sessão (valor do cookie httpOnly) não volta para o visitante
        Assert.Equal(JsonValueKind.Null, entrada.GetProperty("session").ValueKind);
        Assert.StartsWith("latency; rule=lenta", entrada.GetProperty("fault").GetString());

        Assert.Empty((await Body(await app.Visitor().GetAsync("/_chaos/log"))).EnumerateArray());
        Assert.Equal(2, (await Body(await app.Admin().GetAsync("/_chaos/log"))).GetArrayLength());
    }

    // ---------- cada tipo de falha ----------

    [Fact]
    public async Task Html_no_lugar_de_json()
    {
        await using var app = new ChaosFactory();
        var c = app.Visitor();
        await c.PutAsync("/_chaos/session", Json(new { scenario = "html" }));
        var res = await c.GetAsync("/api/tickets");
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType!.MediaType);
        Assert.Contains("nginx", await res.Content.ReadAsStringAsync());
        Assert.Equal("html-error; rule=html; status=502", Fault(res));
    }

    [Fact]
    public async Task Rate_limit_injetado_devolve_429_com_retry_after()
    {
        await using var app = new ChaosFactory();
        var c = app.Visitor();
        await c.PutAsync("/_chaos/session", Json(new { scenario = "limite" }));
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/tickets")).StatusCode);

        var res = await c.GetAsync("/api/tickets");
        Assert.Equal((HttpStatusCode)429, res.StatusCode);
        Assert.InRange(res.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 5);
        Assert.StartsWith("rate-limit; rule=limite", Fault(res));
    }

    [Fact]
    public async Task Timeout_segura_e_devolve_504_e_cliente_que_desiste_nao_quebra_nada()
    {
        await using var app = new ChaosFactory();
        await app.Admin().PutAsync("/_chaos/rules", Json(new { rules = new[] { new { name = "trava", kind = "timeout", holdMs = 300 } } }));
        var c = app.Visitor();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = await c.GetAsync("/api/tickets");
        Assert.Equal(HttpStatusCode.GatewayTimeout, res.StatusCode);
        Assert.True(sw.ElapsedMilliseconds >= 250, $"respondeu em {sw.ElapsedMilliseconds} ms");
        Assert.Equal("timeout; rule=trava; ms=300", Fault(res));

        using var curto = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetAsync("/api/tickets", curto.Token));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/_chaos/health")).StatusCode);
    }

    [Fact]
    public async Task Queda_de_conexao_chega_como_excecao_no_cliente()
    {
        await using var app = new ChaosFactory();
        await app.Admin().PutAsync("/_chaos/rules", Json(new { rules = new[] { new { name = "cai", kind = "drop" } } }));
        await Assert.ThrowsAnyAsync<Exception>(() => app.Visitor().GetAsync("/api/tickets"));
    }

    [Fact]
    public async Task Probabilidade_zero_nunca_aplica()
    {
        await using var app = new ChaosFactory(random: 0.999);
        await app.Admin().PutAsync("/_chaos/rules/scenario/instavel", null);
        var res = await app.Visitor().GetAsync("/api/tickets");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Null(Fault(res));
    }

    // ---------- API real (YARP) ----------

    [Fact]
    public async Task Repassa_para_a_API_real_com_x_forwarded_for_e_sem_o_cookie_da_demo()
    {
        await using var echo = await EchoApi.StartAsync();
        await using var app = new ChaosFactory(new() { ["UPSTREAM_URL"] = echo.Url });
        await app.Admin().PutAsync("/_chaos/rules", Json(new { rules = new[] { new { name = "lenta", kind = "latency", delayMinMs = 10, delayMaxMs = 20 } } }));

        var c = app.Visitor();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/tickets?page=3");
        req.Headers.Add("Cookie", "chaos_sessao=abc; outro=1");
        req.Headers.Add("X-Chaos-Forjado", "1");
        var res = await c.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("latency; rule=lenta; ms=10", Fault(res));
        var body = await Body(res);
        Assert.Equal("/api/tickets", body.GetProperty("path").GetString());
        Assert.Equal("?page=3", body.GetProperty("query").GetString());
        var headers = body.GetProperty("headers");
        Assert.Equal("outro=1", headers.GetProperty("Cookie").GetString());
        Assert.False(headers.TryGetProperty("X-Chaos-Forjado", out _));
        Assert.True(headers.TryGetProperty("X-Forwarded-Proto", out _));

        // com API real, a raiz é da API (nada de redirecionar para a demo)
        Assert.Equal("/", (await Body(await c.GetAsync("/"))).GetProperty("path").GetString());
    }

    [Fact]
    public async Task API_real_fora_do_ar_vira_502_sem_a_marca_de_falha_injetada()
    {
        await using var app = new ChaosFactory(new() { ["UPSTREAM_URL"] = "http://127.0.0.1:1" });
        var res = await app.Visitor().GetAsync("/api/tickets");
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Null(Fault(res));
        Assert.Equal("upstream_unavailable", (await Body(res)).GetProperty("error").GetProperty("code").GetString());
    }

    // ---------- limites do próprio proxy e documentação ----------

    [Fact]
    public async Task Limite_do_proprio_proxy_devolve_429_sem_marca()
    {
        await using var app = new ChaosFactory(new() { ["PROXY_RATE_LIMIT"] = "3" });
        var c = app.Visitor();
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/tickets")).StatusCode);
        var res = await c.GetAsync("/api/tickets");
        Assert.Equal((HttpStatusCode)429, res.StatusCode);
        Assert.NotNull(res.Headers.RetryAfter);
        Assert.Null(Fault(res));
    }

    [Fact]
    public async Task Toda_rota_de_controle_esta_no_OpenAPI()
    {
        await using var app = new ChaosFactory();
        var doc = await Body(await app.Visitor().GetAsync("/_chaos/openapi.json"));
        var paths = doc.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Select(m => $"{m.Name.ToUpperInvariant()} {p.Name}"))
            .ToHashSet();

        string[] esperado =
        [
            "GET /_chaos/health", "GET /_chaos/scenarios", "GET /_chaos/session", "PUT /_chaos/session", "DELETE /_chaos/session",
            "GET /_chaos/log", "GET /_chaos/rules", "PUT /_chaos/rules", "PUT /_chaos/rules/scenario/{name}", "DELETE /_chaos/rules",
        ];
        Assert.Empty(esperado.Except(paths));
    }
}
