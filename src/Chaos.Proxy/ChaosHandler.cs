using System.Diagnostics;
using System.Globalization;
using Chaos.Domain;

namespace Chaos.Proxy;

/// <summary>
/// Toda requisição que não é da área /_chaos passa aqui: decide a falha, aplica e, se for o caso,
/// repassa para a API. Registra o resultado no log.
/// </summary>
public sealed class ChaosHandler(ChaosEngine engine, RuleStore rules, SessionStore sessions, IUpstream upstream, RequestLog log) : IDisposable
{
    public void Dispose() => _holds.Dispose();

    /// <summary>Quantas requisições podem ficar "travadas" ao mesmo tempo. Acima disso, 503 na hora.</summary>
    public const int MaxHolds = 100;

    private readonly SemaphoreSlim _holds = new(MaxHolds, MaxHolds);

    public async Task HandleAsync(HttpContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var sessionId = ctx.Request.Cookies[SessionStore.Cookie];
        var scenario = sessions.Get(sessionId);
        var ruleSet = scenario is not null ? ScenarioRules(scenario) : rules.Current;
        var clientKey = scenario is not null ? $"s:{sessionId}" : ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

        var decision = engine.Decide(new ChaosRequest(ctx.Request.Method, ctx.Request.Path.Value ?? "/", clientKey), ruleSet);
        if (decision.Marker is { } marker)
            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers[Responses.FaultHeader] = marker;
                return Task.CompletedTask;
            });

        var status = 0;
        try
        {
            switch (decision)
            {
                case ChaosDecision.Drop:
                    ctx.Abort();
                    break;

                case ChaosDecision.Respond r when r.Kind == "html-error":
                    await Responses.WriteProxyHtmlAsync(ctx, r.Status);
                    break;

                case ChaosDecision.Respond { Status: 429 } r:
                    ctx.Response.Headers.RetryAfter = r.RetryAfterSeconds!.Value.ToString(CultureInfo.InvariantCulture);
                    await Responses.WriteErrorAsync(ctx, 429, "rate_limited", $"Muitas requisições. Tente de novo em {r.RetryAfterSeconds} s. (falha injetada: {r.Rule})");
                    break;

                case ChaosDecision.Respond r:
                    await Responses.WriteErrorAsync(ctx, r.Status, "injected_fault", $"Falha injetada pelo Chaos Proxy (regra {r.Rule}).");
                    break;

                case ChaosDecision.Hold h:
                    await HoldAsync(ctx, h);
                    break;

                case ChaosDecision.Forward f:
                    if (f.DelayMs > 0) await Task.Delay(f.DelayMs, ctx.RequestAborted);
                    await upstream.ForwardAsync(ctx);
                    break;
            }
            status = ctx.RequestAborted.IsCancellationRequested ? 0 : ctx.Response.StatusCode;
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // o cliente desistiu (timeout dele): normal num teste de caos
        }
        finally
        {
            var path = ctx.Request.Path.Value ?? "/";
            log.Add(new LogEntry(DateTimeOffset.UtcNow, ctx.Request.Method, path.Length > 200 ? path[..200] : path,
                status, sw.ElapsedMilliseconds, decision.Marker, scenario is not null ? sessionId : null));
        }
    }

    private async Task HoldAsync(HttpContext ctx, ChaosDecision.Hold h)
    {
        // segurar conexões custa memória e sockets: com o teto, ninguém derruba o proxy assim
        if (!await _holds.WaitAsync(0))
        {
            await Responses.WriteErrorAsync(ctx, 503, "chaos_busy", "Muitas requisições presas ao mesmo tempo no proxy.");
            return;
        }
        try
        {
            await Task.Delay(h.HoldMs, ctx.RequestAborted);
            await Responses.WriteErrorAsync(ctx, 504, "injected_timeout", $"A API não respondeu em {h.HoldMs} ms (falha injetada: {h.Rule}).");
        }
        finally
        {
            _holds.Release();
        }
    }

    // RuleSet de cada cenário pronto, criado uma vez só
    private static readonly Dictionary<string, RuleSet> ScenarioSets =
        Scenarios.All.ToDictionary(s => s.Name, s => new RuleSet(s.Rules, s.Name));

    public static RuleSet ScenarioRules(Scenario s) => ScenarioSets[s.Name];
}
