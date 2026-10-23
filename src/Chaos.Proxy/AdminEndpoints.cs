using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chaos.Domain;

namespace Chaos.Proxy;

/// <summary>Corpo do PUT /_chaos/rules.</summary>
public sealed record RulesInput(IReadOnlyList<FaultRule>? Rules);

/// <summary>Corpo do PUT /_chaos/session.</summary>
public sealed record SessionInput(string? Scenario);

/// <summary>
/// Token de administração: comparado em tempo constante (pelo hash, para os tamanhos baterem) e
/// com freio: 5 tentativas erradas em 15 min por IP, depois 429 até a janela virar.
/// </summary>
public sealed class AdminAuth(ChaosSettings settings, IClock clock)
{
    public const int MaxFailures = 5;
    public const int WindowSeconds = 15 * 60;

    private readonly WindowCounter _failures = new(clock);
    private readonly ConcurrentBlocked _blocked = new();
    private readonly byte[]? _expected = settings.AdminToken is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(settings.AdminToken));

    public bool Enabled => _expected is not null;

    /// <summary>Nulo se autorizado; senão, a resposta de erro.</summary>
    public IResult? Check(HttpContext ctx)
    {
        if (_expected is null)
            return Responses.Error(401, "admin_disabled", "A administração está desligada: configure CHAOS_ADMIN_TOKEN.");

        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        var now = clock.UtcNow.ToUnixTimeSeconds();
        if (_blocked.Until(ip) is { } until && until > now)
        {
            ctx.Response.Headers.RetryAfter = (until - now).ToString(CultureInfo.InvariantCulture);
            return Responses.Error(429, "too_many_attempts", "Muitas tentativas com token errado. Espere e tente de novo.");
        }

        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
        if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), _expected)) return null;

        var (count, reset) = _failures.Hit($"auth|{ip}", WindowSeconds);
        if (count >= MaxFailures) _blocked.Block(ip, now + reset);
        return Responses.Error(401, "unauthorized", "Envie o header Authorization: Bearer <token de administração>.");
    }

    /// <summary>IPs bloqueados até um instante (unix), com limpeza simples.</summary>
    private sealed class ConcurrentBlocked
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _until = new();

        public long? Until(string ip) => _until.TryGetValue(ip, out var u) ? u : null;

        public void Block(string ip, long until)
        {
            if (_until.Count > WindowCounter.MaxKeys) _until.Clear();
            _until[ip] = until;
        }
    }
}

public static class AdminEndpoints
{
    public const int MaxBodyBytes = 64 * 1024;

    public static void MapChaosApi(this WebApplication app)
    {
        var api = app.MapGroup("/_chaos").WithTags("Chaos Proxy");

        api.MapGet("/health", (ChaosSettings s, RuleStore rules, IUpstream upstream, AdminAuth auth) => Results.Ok(new
        {
            status = "ok",
            upstream = upstream.Kind,
            scenario = rules.Current.Scenario,
            rules = rules.Current.Rules.Count,
            admin = auth.Enabled,
        }))
        .WithSummary("Saúde do proxy: para onde repassa e quantas regras globais estão ativas")
        .RequireRateLimiting(RateLimits.Read);

        api.MapGet("/scenarios", () => Results.Ok(Scenarios.All.Select(s => new { s.Name, s.Title, s.Description, s.Rules })))
            .WithSummary("Cenários prontos")
            .RequireRateLimiting(RateLimits.Read);

        // ---- sessão da demo: cada visitante escolhe o próprio cenário ----
        api.MapGet("/session", (HttpContext ctx, SessionStore sessions) =>
            Results.Ok(new { scenario = sessions.Get(ctx.Request.Cookies[SessionStore.Cookie])?.Name }))
            .WithSummary("Cenário escolhido por este visitante (ou nulo)")
            .RequireRateLimiting(RateLimits.Read);

        api.MapPut("/session", async (HttpContext ctx, SessionStore sessions) =>
        {
            var (input, error) = await ReadAsync<SessionInput>(ctx);
            if (error is not null) return error;
            var scenario = Scenarios.Find(input!.Scenario);
            if (scenario is null)
                return Responses.Error(422, "validation_error", "Dados inválidos",
                    new Dictionary<string, string> { ["scenario"] = "Use um dos cenários de /_chaos/scenarios" });

            var id = ctx.Request.Cookies[SessionStore.Cookie];
            if (id is null || id.Length != 22)
            {
                id = SessionStore.NewId();
                ctx.Response.Cookies.Append(SessionStore.Cookie, id, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = ctx.Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    MaxAge = TimeSpan.FromHours(1),
                    Path = "/",
                });
            }
            sessions.Set(id, scenario);
            return Results.Ok(new { scenario = scenario.Name });
        })
        .WithSummary("Escolhe um cenário pronto só para este visitante (cookie de sessão)")
        .RequireRateLimiting(RateLimits.Write);

        api.MapDelete("/session", (HttpContext ctx, SessionStore sessions) =>
        {
            if (ctx.Request.Cookies[SessionStore.Cookie] is { } id) sessions.Remove(id);
            ctx.Response.Cookies.Delete(SessionStore.Cookie);
            return Results.NoContent();
        })
        .WithSummary("Volta ao normal para este visitante")
        .RequireRateLimiting(RateLimits.Write);

        api.MapGet("/log", (HttpContext ctx, RequestLog log, AdminAuth auth, SessionStore sessions) =>
        {
            // o administrador vê tudo; o visitante, só as próprias requisições
            if (ctx.Request.Headers.Authorization.Count > 0)
                return auth.Check(ctx) ?? Results.Ok(log.Snapshot());
            // sem o id da sessão na resposta: o cookie é httpOnly justamente para o JavaScript não ler esse valor
            var id = ctx.Request.Cookies[SessionStore.Cookie];
            return Results.Ok(id is null ? [] : log.Snapshot(e => e.Session == id).Select(e => e with { Session = null }).ToList());
        })
        .WithSummary($"Últimas requisições (até {RequestLog.Capacity})")
        .RequireRateLimiting(RateLimits.Read);

        // ---- administração (token): regras globais, valem para todo mundo ----
        api.MapGet("/rules", (HttpContext ctx, AdminAuth auth, RuleStore rules) =>
            auth.Check(ctx) ?? Results.Ok(new { scenario = rules.Current.Scenario, rules = rules.Current.Rules }))
            .WithSummary("Regras globais (administração)")
            .RequireRateLimiting(RateLimits.Read);

        api.MapPut("/rules", async (HttpContext ctx, AdminAuth auth, RuleStore rules) =>
        {
            if (auth.Check(ctx) is { } denied) return denied;
            var (input, error) = await ReadAsync<RulesInput>(ctx);
            if (error is not null) return error;
            var list = input!.Rules ?? [];
            var errors = RuleValidator.Validate(list);
            if (errors.Count > 0) return Responses.Error(422, "validation_error", "Dados inválidos", errors);
            rules.Replace(new RuleSet(list, "personalizado"));
            return Results.Ok(new { scenario = "personalizado", rules = list });
        })
        .WithSummary("Troca todas as regras globais (administração)")
        .RequireRateLimiting(RateLimits.Write);

        api.MapPut("/rules/scenario/{name}", (string name, HttpContext ctx, AdminAuth auth, RuleStore rules) =>
        {
            if (auth.Check(ctx) is { } denied) return denied;
            var scenario = Scenarios.Find(name);
            if (scenario is null) return Responses.Error(404, "not_found", "Cenário não encontrado");
            rules.Replace(ChaosHandler.ScenarioRules(scenario));
            return Results.Ok(new { scenario = scenario.Name, rules = scenario.Rules });
        })
        .WithSummary("Aplica um cenário pronto para todo mundo (administração)")
        .RequireRateLimiting(RateLimits.Write);

        api.MapDelete("/rules", (HttpContext ctx, AdminAuth auth, RuleStore rules) =>
        {
            if (auth.Check(ctx) is { } denied) return denied;
            rules.Replace(RuleSet.Empty);
            return Results.NoContent();
        })
        .WithSummary("Remove todas as regras globais (administração)")
        .RequireRateLimiting(RateLimits.Write);
    }

    /// <summary>Lê o JSON com limite de tamanho; JSON inválido ou campo desconhecido viram 400.</summary>
    private static async Task<(T? Value, IResult? Error)> ReadAsync<T>(HttpContext ctx) where T : class
    {
        if (ctx.Request.ContentLength > MaxBodyBytes)
            return (null, Responses.Error(413, "payload_too_large", $"Corpo maior que {MaxBodyBytes / 1024} KB"));
        if (ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            return (null, Responses.Error(415, "unsupported_media_type", "Envie Content-Type: application/json"));

        try
        {
            using var limited = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                if (limited.Length + read > MaxBodyBytes)
                    return (null, Responses.Error(413, "payload_too_large", $"Corpo maior que {MaxBodyBytes / 1024} KB"));
                limited.Write(buffer, 0, read);
            }
            limited.Position = 0;
            var value = await JsonSerializer.DeserializeAsync<T>(limited, Responses.Json, ctx.RequestAborted);
            return value is null ? (null, Responses.Error(400, "invalid_json", "Corpo vazio")) : (value, null);
        }
        catch (JsonException e)
        {
            // a mensagem do serializador diz o campo (ex.: "rules[0].kind"), sem expor nada interno
            return (null, Responses.Error(400, "invalid_json", "JSON inválido", new Dictionary<string, string> { ["body"] = e.Path is { } p ? $"Problema em {p}" : "Não foi possível ler o JSON" }));
        }
    }
}
