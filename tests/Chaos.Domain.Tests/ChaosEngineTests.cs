using Chaos.Domain;

namespace Chaos.Domain.Tests;

public class ChaosEngineTests
{
    private static readonly ChaosRequest Get = new("GET", "/api/tickets", "1.2.3.4");
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

    private ChaosEngine Engine(IRandomSource random) => new(random, new WindowCounter(_clock));

    [Fact]
    public void Sem_regras_repassa_sem_marca()
    {
        var d = Engine(new ScriptedRandom(0)).Decide(Get, RuleSet.Empty);
        Assert.Equal(ChaosDecision.PassThrough, d);
        Assert.Null(d.Marker);
    }

    [Theory]
    [InlineData(0.19, true)]
    [InlineData(0.20, false)]
    [InlineData(0.99, false)]
    public void Probabilidade_decide_pelo_sorteio(double sorteio, bool aplica)
    {
        var rules = new RuleSet([new FaultRule { Name = "e", Kind = FaultKind.Error, Status = 503, Probability = 0.2 }]);
        var d = Engine(new ScriptedRandom(sorteio)).Decide(Get, rules);
        Assert.Equal(aplica, d is ChaosDecision.Respond { Status: 503 });
    }

    [Fact]
    public void Probabilidade_zero_nunca_aplica()
    {
        var rules = new RuleSet([new FaultRule { Name = "e", Kind = FaultKind.Error, Status = 500, Probability = 0 }]);
        Assert.Equal(ChaosDecision.PassThrough, Engine(new ScriptedRandom(0)).Decide(Get, rules));
    }

    [Fact]
    public void Latencia_sorteia_dentro_da_faixa_e_marca()
    {
        var random = new ScriptedRandom(0, 0.5);
        var rules = new RuleSet([new FaultRule { Name = "lenta", Kind = FaultKind.Latency, DelayMinMs = 1000, DelayMaxMs = 3000 }]);
        var d = Assert.IsType<ChaosDecision.Forward>(Engine(random).Decide(Get, rules));
        Assert.Equal((1000, 3000), (random.LastMin, random.LastMax));
        Assert.Equal(2000, d.DelayMs);
        Assert.Equal("latency; rule=lenta; ms=2000", d.Marker);
    }

    [Fact]
    public void Primeira_regra_que_aplica_decide()
    {
        var rules = new RuleSet(
        [
            new FaultRule { Name = "a", Kind = FaultKind.Error, Status = 500, Probability = 0.5 },
            new FaultRule { Name = "b", Kind = FaultKind.Drop },
        ]);
        // 1º sorteio 0.9 (não aplica "a"), 2º 0.9 (b tem probabilidade 1)
        Assert.IsType<ChaosDecision.Drop>(Engine(new ScriptedRandom(0.9)).Decide(Get, rules));
        Assert.IsType<ChaosDecision.Respond>(Engine(new ScriptedRandom(0.1)).Decide(Get, rules));
    }

    [Fact]
    public void Filtra_por_metodo_e_rota()
    {
        var rules = new RuleSet([new FaultRule { Name = "post", Kind = FaultKind.Drop, Method = "POST", Path = "/api/tickets" }]);
        var engine = Engine(new ScriptedRandom(0));
        Assert.Equal(ChaosDecision.PassThrough, engine.Decide(Get, rules));
        Assert.Equal(ChaosDecision.PassThrough, engine.Decide(new("POST", "/api/tickets/7/comments", "x"), rules));
        Assert.IsType<ChaosDecision.Drop>(engine.Decide(new("POST", "/api/tickets", "x"), rules));
    }

    [Fact]
    public void Rate_limit_conta_por_cliente_e_libera_na_proxima_janela()
    {
        var engine = Engine(new ScriptedRandom(0));
        var rules = new RuleSet([new FaultRule { Name = "limite", Kind = FaultKind.RateLimit, Limit = 2, WindowSeconds = 10 }], "limite");

        Assert.Equal(ChaosDecision.PassThrough, engine.Decide(Get, rules));
        Assert.Equal(ChaosDecision.PassThrough, engine.Decide(Get, rules));
        var bloqueado = Assert.IsType<ChaosDecision.Respond>(engine.Decide(Get, rules));
        Assert.Equal(429, bloqueado.Status);
        Assert.Equal(10, bloqueado.RetryAfterSeconds);

        // outro cliente tem a própria conta
        Assert.Equal(ChaosDecision.PassThrough, engine.Decide(Get with { ClientKey = "5.6.7.8" }, rules));

        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(6, Assert.IsType<ChaosDecision.Respond>(engine.Decide(Get, rules)).RetryAfterSeconds);

        _clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(ChaosDecision.PassThrough, engine.Decide(Get, rules));
    }

    [Fact]
    public void Timeout_e_html_viram_as_decisoes_certas()
    {
        var engine = Engine(new ScriptedRandom(0));
        var hold = engine.Decide(Get, new RuleSet([new FaultRule { Name = "t", Kind = FaultKind.Timeout, HoldMs = 5000 }]));
        Assert.Equal(new ChaosDecision.Hold(5000, "t"), hold);
        var html = engine.Decide(Get, new RuleSet([new FaultRule { Name = "h", Kind = FaultKind.HtmlError, Status = 502 }]));
        Assert.Equal("html-error; rule=h; status=502", html.Marker);
    }

    [Fact]
    public void Contador_nao_cresce_para_sempre()
    {
        var counter = new WindowCounter(_clock);
        for (var i = 0; i < WindowCounter.MaxKeys + 50; i++) counter.Hit($"k{i}", 10);
        Assert.True(counter.Keys <= WindowCounter.MaxKeys);
    }
}
