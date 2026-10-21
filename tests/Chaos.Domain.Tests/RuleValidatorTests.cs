using Chaos.Domain;

namespace Chaos.Domain.Tests;

public class RuleValidatorTests
{
    private static Dictionary<string, string> Errors(params FaultRule[] rules) => RuleValidator.Validate(rules);

    [Fact]
    public void Todos_os_cenarios_prontos_sao_validos()
    {
        foreach (var s in Scenarios.All)
            Assert.Empty(RuleValidator.Validate(s.Rules));
    }

    [Fact]
    public void Nome_precisa_ser_simples_e_unico()
    {
        var e = Errors(
            new FaultRule { Name = "Lenta!", Kind = FaultKind.Drop },
            new FaultRule { Name = "ok", Kind = FaultKind.Drop },
            new FaultRule { Name = "ok", Kind = FaultKind.Drop });
        Assert.True(e.ContainsKey("rules[0].name"));
        Assert.Equal("Nome repetido", e["rules[2].name"]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(500, 100)]
    [InlineData(0, 30_001)]
    [InlineData(-1, 10)]
    public void Latencia_fora_dos_limites(int min, int max)
    {
        var e = Errors(new FaultRule { Name = "l", Kind = FaultKind.Latency, DelayMinMs = min, DelayMaxMs = max });
        Assert.True(e.ContainsKey("rules[0].delayMaxMs"));
    }

    [Theory]
    [InlineData(FaultKind.Error, 200)]
    [InlineData(FaultKind.Error, 429)]
    [InlineData(FaultKind.Error, 600)]
    [InlineData(FaultKind.HtmlError, 404)]
    public void Status_invalido(FaultKind kind, int status)
    {
        Assert.True(Errors(new FaultRule { Name = "e", Kind = kind, Status = status }).ContainsKey("rules[0].status"));
    }

    [Fact]
    public void Probabilidade_metodo_e_rota()
    {
        var e = Errors(new FaultRule { Name = "x", Kind = FaultKind.Drop, Probability = 1.5, Method = "get", Path = "api" });
        Assert.True(e.ContainsKey("rules[0].probability"));
        Assert.True(e.ContainsKey("rules[0].method"));
        Assert.True(e.ContainsKey("rules[0].path"));
    }

    [Fact]
    public void Rate_limit_e_timeout_com_limites()
    {
        var e = Errors(
            new FaultRule { Name = "r", Kind = FaultKind.RateLimit, Limit = 0, WindowSeconds = 7200 },
            new FaultRule { Name = "t", Kind = FaultKind.Timeout, HoldMs = 60_000 });
        Assert.True(e.ContainsKey("rules[0].limit"));
        Assert.True(e.ContainsKey("rules[0].windowSeconds"));
        Assert.True(e.ContainsKey("rules[1].holdMs"));
    }

    [Fact]
    public void No_maximo_20_regras()
    {
        var rules = Enumerable.Range(0, 21).Select(i => new FaultRule { Name = $"r{i}", Kind = FaultKind.Drop }).ToArray();
        Assert.True(RuleValidator.Validate(rules).ContainsKey("rules"));
    }
}
