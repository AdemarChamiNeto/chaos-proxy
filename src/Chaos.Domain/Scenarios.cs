namespace Chaos.Domain;

/// <summary>Um conjunto de regras pronto, com nome e descrição para a demo.</summary>
public sealed record Scenario(string Name, string Title, string Description, IReadOnlyList<FaultRule> Rules);

/// <summary>
/// Cenários prontos. São os únicos que o visitante da demo pode escolher; regras livres só pela
/// área de administração (com token).
/// </summary>
public static class Scenarios
{
    public static readonly IReadOnlyList<Scenario> All =
    [
        new("normal", "Normal", "Sem falhas: o proxy só repassa.", []),
        new("lenta", "API lenta", "Toda requisição demora de 1,5 a 4 s.",
        [
            new FaultRule { Name = "lenta", Kind = FaultKind.Latency, DelayMinMs = 1500, DelayMaxMs = 4000 },
        ]),
        new("instavel", "API instável", "20% de erro 503, 10% de queda de conexão e 30% de lentidão (300 ms a 2 s).",
        // a ordem importa: cada regra só sorteia se a anterior não aplicou. 20% de 503; dos 80% que
        // sobram, 12,5% caem (10% do total); dos 70% restantes, 43% atrasam (30% do total).
        [
            new FaultRule { Name = "instavel-503", Kind = FaultKind.Error, Status = 503, Probability = 0.2 },
            new FaultRule { Name = "instavel-queda", Kind = FaultKind.Drop, Probability = 0.125 },
            new FaultRule { Name = "instavel-lenta", Kind = FaultKind.Latency, DelayMinMs = 300, DelayMaxMs = 2000, Probability = 0.43 },
        ]),
        new("fora", "API fora do ar", "Toda requisição recebe 503.",
        [
            new FaultRule { Name = "fora", Kind = FaultKind.Error, Status = 503 },
        ]),
        new("travada", "API travada", "A API não responde: segura 10 s e devolve 504 (o cliente deve desistir antes).",
        [
            new FaultRule { Name = "travada", Kind = FaultKind.Timeout, HoldMs = 10_000 },
        ]),
        new("limite", "Rate limit", "Até 5 requisições a cada 5 s por cliente; depois, 429 com Retry-After.",
        [
            new FaultRule { Name = "limite", Kind = FaultKind.RateLimit, Limit = 5, WindowSeconds = 5 },
        ]),
        new("html", "HTML no lugar de JSON", "Metade das respostas vira a página de erro 502 de um proxy (aconteceu de verdade com o Portal).",
        [
            new FaultRule { Name = "html", Kind = FaultKind.HtmlError, Status = 502, Probability = 0.5 },
        ]),
    ];

    public static Scenario? Find(string? name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
