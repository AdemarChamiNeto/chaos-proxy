using System.Collections.Concurrent;

namespace Chaos.Domain;

/// <summary>Relógio injetável: os testes avançam o tempo sem esperar.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Sorteio injetável: os testes fixam o resultado.</summary>
public interface IRandomSource
{
    /// <summary>Número em [0, 1).</summary>
    double NextDouble();

    /// <summary>Inteiro em [min, max] (os dois inclusive).</summary>
    int Next(int min, int max);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class SystemRandom : IRandomSource
{
    public double NextDouble() => Random.Shared.NextDouble();
    public int Next(int min, int max) => Random.Shared.Next(min, max + 1);
}

/// <summary>O que fazer com a requisição.</summary>
public abstract record ChaosDecision
{
    /// <summary>Valor do cabeçalho X-Chaos-Fault, ou nulo quando nada foi injetado.</summary>
    public abstract string? Marker { get; }

    /// <summary>Repassa para a API depois de <see cref="DelayMs"/> (0 = sem atraso).</summary>
    public sealed record Forward(int DelayMs, string? Rule) : ChaosDecision
    {
        public override string? Marker => Rule is null ? null : $"latency; rule={Rule}; ms={DelayMs}";
    }

    /// <summary>Responde sem chamar a API.</summary>
    public sealed record Respond(int Status, string Rule, string Kind, int? RetryAfterSeconds = null) : ChaosDecision
    {
        public override string? Marker => $"{Kind}; rule={Rule}; status={Status}";
    }

    /// <summary>Segura a resposta por <see cref="HoldMs"/> e devolve 504.</summary>
    public sealed record Hold(int HoldMs, string Rule) : ChaosDecision
    {
        public override string? Marker => $"timeout; rule={Rule}; ms={HoldMs}";
    }

    /// <summary>Derruba a conexão.</summary>
    public sealed record Drop(string Rule) : ChaosDecision
    {
        public override string? Marker => $"drop; rule={Rule}";
    }

    public static readonly ChaosDecision PassThrough = new Forward(0, null);
}

/// <summary>Dados da requisição que importam para a decisão.</summary>
/// <param name="ClientKey">Quem pede (IP ou sessão da demo): o rate limit conta por cliente.</param>
public sealed record ChaosRequest(string Method, string Path, string ClientKey);

/// <summary>Conjunto de regras imutável. Trocar as regras é trocar a referência, sem corrida.</summary>
public sealed class RuleSet
{
    public static readonly RuleSet Empty = new([], "normal");

    public RuleSet(IReadOnlyList<FaultRule> rules, string? scenario = null)
    {
        Rules = rules;
        Scenario = scenario;
        Patterns = rules.Select(r => PathPattern.Parse(r.Path)).ToArray();
    }

    public IReadOnlyList<FaultRule> Rules { get; }
    public string? Scenario { get; }
    internal PathPattern[] Patterns { get; }
}

/// <summary>Contagem por janela fixa. Guarda no máximo <see cref="MaxKeys"/> chaves (as velhas saem).</summary>
public sealed class WindowCounter(IClock clock)
{
    public const int MaxKeys = 10_000;

    private readonly ConcurrentDictionary<string, Window> _windows = new();

    private sealed class Window(long start)
    {
        public long Start = start;
        public int Count;
    }

    /// <summary>Soma uma ocorrência. Devolve o total da janela atual e os segundos até ela virar.</summary>
    public (int Count, int SecondsToReset) Hit(string key, int windowSeconds)
    {
        var now = clock.UtcNow.ToUnixTimeSeconds();
        var start = now - (now % windowSeconds);
        if (_windows.Count >= MaxKeys) Prune(now);

        var w = _windows.GetOrAdd(key, _ => new Window(start));
        lock (w)
        {
            if (w.Start != start)
            {
                w.Start = start;
                w.Count = 0;
            }
            w.Count++;
            return (w.Count, (int)(start + windowSeconds - now));
        }
    }

    public int Keys => _windows.Count;

    private void Prune(long now)
    {
        // janelas de até 1 h: o que começou há mais de 1 h já venceu
        foreach (var (key, w) in _windows)
            if (now - w.Start > 3600) _windows.TryRemove(key, out _);
        if (_windows.Count >= MaxKeys) _windows.Clear();
    }
}

/// <summary>Decide a falha de cada requisição a partir das regras. Não conhece HTTP nem ASP.NET.</summary>
public sealed class ChaosEngine(IRandomSource random, WindowCounter counter)
{
    public ChaosDecision Decide(ChaosRequest request, RuleSet rules)
    {
        for (var i = 0; i < rules.Rules.Count; i++)
        {
            var rule = rules.Rules[i];
            if (rule.Method is not null && !string.Equals(rule.Method, request.Method, StringComparison.OrdinalIgnoreCase)) continue;
            if (!rules.Patterns[i].Matches(request.Path)) continue;

            if (rule.Kind == FaultKind.RateLimit)
            {
                var (count, reset) = counter.Hit($"{rules.Scenario}|{rule.Name}|{request.ClientKey}", rule.WindowSeconds);
                if (count > rule.Limit) return new ChaosDecision.Respond(429, rule.Name, "rate-limit", Math.Max(1, reset));
                continue;
            }

            // probabilidade 1 sempre aplica; 0 nunca (NextDouble() é < 1)
            if (random.NextDouble() >= rule.Probability) continue;

            return rule.Kind switch
            {
                FaultKind.Latency => new ChaosDecision.Forward(random.Next(rule.DelayMinMs, rule.DelayMaxMs), rule.Name),
                FaultKind.Timeout => new ChaosDecision.Hold(rule.HoldMs, rule.Name),
                FaultKind.Error => new ChaosDecision.Respond(rule.Status, rule.Name, "error"),
                FaultKind.HtmlError => new ChaosDecision.Respond(rule.Status, rule.Name, "html-error"),
                FaultKind.Drop => new ChaosDecision.Drop(rule.Name),
                _ => ChaosDecision.PassThrough,
            };
        }
        return ChaosDecision.PassThrough;
    }
}
