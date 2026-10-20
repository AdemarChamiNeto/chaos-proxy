using System.Net;
using Yarp.ReverseProxy.Forwarder;

namespace Chaos.Proxy;

/// <summary>Para onde a requisição vai depois de passar pelo caos.</summary>
public interface IUpstream
{
    string Kind { get; }
    Task ForwardAsync(HttpContext ctx);
}

/// <summary>
/// API real (ex.: a API de Chamados), via YARP. Uma falha de verdade (API fora, DNS, recusa de
/// conexão) vira 502 <b>sem</b> o cabeçalho X-Chaos-Fault: assim ninguém confunde teste com problema real.
/// </summary>
public sealed class YarpUpstream(IHttpForwarder forwarder, ChaosSettings settings) : IUpstream, IDisposable
{
    private readonly HttpMessageInvoker _client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ActivityHeadersPropagator = null,
    });

    private static readonly ForwarderRequestConfig Config = new() { ActivityTimeout = TimeSpan.FromSeconds(30) };

    public string Kind => "real";

    public async Task ForwardAsync(HttpContext ctx)
    {
        var error = await forwarder.SendAsync(ctx, settings.Upstream!.ToString(), _client, Config, ForwardTransformer.Instance);
        if (error != ForwarderError.None && !ctx.Response.HasStarted && !ctx.RequestAborted.IsCancellationRequested)
        {
            var timeout = error is ForwarderError.RequestTimedOut;
            await Responses.WriteErrorAsync(ctx, timeout ? 504 : 502, timeout ? "upstream_timeout" : "upstream_unavailable",
                timeout ? "A API demorou demais para responder (falha real, não injetada)." : "O proxy não conseguiu falar com a API (falha real, não injetada).");
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>
/// Ajusta o que segue para a API: tira o cookie da demo e qualquer X-Chaos-* que o cliente mande,
/// e informa o IP de quem chamou o proxy (X-Forwarded-For). A API só deve confiar nesse cabeçalho
/// se souber que está atrás do proxy; a API de Chamados usa só o REMOTE_ADDR, o que está certo.
/// </summary>
public sealed class ForwardTransformer : HttpTransformer
{
    public static readonly ForwardTransformer Instance = new();

    public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        foreach (var name in proxyRequest.Headers.Select(h => h.Key).Where(k => k.StartsWith("X-Chaos-", StringComparison.OrdinalIgnoreCase)).ToList())
            proxyRequest.Headers.Remove(name);

        if (proxyRequest.Headers.TryGetValues("Cookie", out var cookies))
        {
            var kept = string.Join("; ", cookies.SelectMany(c => c.Split(';'))
                .Select(c => c.Trim())
                .Where(c => c.Length > 0 && !c.StartsWith(SessionStore.Cookie + "=", StringComparison.Ordinal)));
            proxyRequest.Headers.Remove("Cookie");
            if (kept.Length > 0) proxyRequest.Headers.TryAddWithoutValidation("Cookie", kept);
        }

        proxyRequest.Headers.Remove("X-Forwarded-For");
        proxyRequest.Headers.Remove("X-Forwarded-Proto");
        proxyRequest.Headers.Remove("X-Forwarded-Host");
        var ip = httpContext.Connection.RemoteIpAddress?.ToString();
        if (ip is not null) proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", httpContext.Request.Scheme);
        proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", httpContext.Request.Host.Value);
    }
}

/// <summary>
/// API falsa embutida, para o modo demonstração: sem UPSTREAM_URL, o proxy abre funcionando.
/// Imita só o formato da API de Chamados (lista paginada, detalhe, criação e erro).
/// </summary>
public sealed class FakeUpstream : IUpstream
{
    private static readonly string[] Titles =
    [
        "Impressora do 2º andar não imprime", "VPN cai a cada 10 minutos", "Sem acesso ao sistema de RH",
        "Notebook não liga", "Pedido de instalação do Power BI", "E-mail não sincroniza no celular",
        "Senha do Wi-Fi de visitantes", "Monitor piscando", "Pasta compartilhada sem permissão", "Teclado com teclas falhando",
    ];

    private static readonly string[] Status = ["aberto", "em_atendimento", "aguardando_usuario", "resolvido"];
    private static readonly string[] Priority = ["P1", "P2", "P3", "P4"];

    public string Kind => "fake";

    private static object Ticket(int id) => new
    {
        id,
        title = Titles[(id - 1) % Titles.Length],
        category = id % 3 == 0 ? "hardware" : id % 3 == 1 ? "software" : "acesso",
        priority = Priority[id % Priority.Length],
        status = Status[id % Status.Length],
        created_at = new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero).AddMinutes(37 * id).ToString("O"),
    };

    public async Task ForwardAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value?.TrimEnd('/') ?? "";
        var method = ctx.Request.Method;

        if (path is "" or "/api" or "/api/health" && HttpMethods.IsGet(method))
        {
            await ctx.Response.WriteAsJsonAsync(new { status = "ok", api = "fake" });
            return;
        }

        if (path == "/api/tickets" && HttpMethods.IsGet(method))
        {
            var page = int.TryParse(ctx.Request.Query["page"], out var p) && p is >= 1 and <= 1000 ? p : 1;
            const int perPage = 10, total = 42;
            var first = (page - 1) * perPage + 1;
            var data = Enumerable.Range(first, Math.Max(0, Math.Min(perPage, total - first + 1))).Select(Ticket);
            await ctx.Response.WriteAsJsonAsync(new { data, meta = new { page, per_page = perPage, total, last_page = (total + perPage - 1) / perPage } });
            return;
        }

        if (path.StartsWith("/api/tickets/", StringComparison.Ordinal) && HttpMethods.IsGet(method))
        {
            if (int.TryParse(path["/api/tickets/".Length..], out var id) && id is >= 1 and <= 42)
                await ctx.Response.WriteAsJsonAsync(Ticket(id));
            else
                await Responses.WriteErrorAsync(ctx, 404, "not_found", "Chamado não encontrado");
            return;
        }

        if (path == "/api/tickets" && HttpMethods.IsPost(method))
        {
            // não guarda nada: a demo é sobre o caminho da requisição, não sobre os dados
            ctx.Response.StatusCode = 201;
            await ctx.Response.WriteAsJsonAsync(new { id = 43, title = "Chamado criado na demonstração", status = "aberto" });
            return;
        }

        await Responses.WriteErrorAsync(ctx, 404, "not_found", "Rota não encontrada na API de demonstração");
    }
}
