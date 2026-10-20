namespace Chaos.Domain;

/// <summary>Tipo de falha que uma regra injeta.</summary>
public enum FaultKind
{
    /// <summary>Atrasa a requisição e depois repassa normalmente.</summary>
    Latency,
    /// <summary>Segura a resposta (o cliente costuma desistir antes) e, no fim, devolve 504.</summary>
    Timeout,
    /// <summary>Responde um erro HTTP em JSON, no formato da API de Chamados.</summary>
    Error,
    /// <summary>Responde uma página HTML de erro, como faz um proxy ou CDN na frente da API.</summary>
    HtmlError,
    /// <summary>Depois de N requisições na janela, responde 429 com Retry-After.</summary>
    RateLimit,
    /// <summary>Derruba a conexão sem resposta.</summary>
    Drop,
}

/// <summary>
/// Uma regra de falha. As regras são avaliadas em ordem: a primeira que casar com a rota e o
/// método, e passar no sorteio de <see cref="Probability"/>, decide o que acontece.
/// </summary>
public sealed record FaultRule
{
    public required string Name { get; init; }
    public required FaultKind Kind { get; init; }

    /// <summary>Padrão de rota: segmentos literais, <c>*</c> (um segmento) e <c>**</c> (qualquer resto).</summary>
    public string Path { get; init; } = "/**";

    /// <summary>Método HTTP, ou nulo para qualquer um.</summary>
    public string? Method { get; init; }

    /// <summary>Chance de 0 a 1. Ignorada no RateLimit, que conta requisições em vez de sortear.</summary>
    public double Probability { get; init; } = 1;

    /// <summary>Latency: atraso mínimo e máximo, em ms.</summary>
    public int DelayMinMs { get; init; }
    public int DelayMaxMs { get; init; }

    /// <summary>Timeout: quanto tempo segurar antes do 504, em ms.</summary>
    public int HoldMs { get; init; }

    /// <summary>Error e HtmlError: status devolvido.</summary>
    public int Status { get; init; }

    /// <summary>RateLimit: quantas requisições por janela, e o tamanho da janela.</summary>
    public int Limit { get; init; }
    public int WindowSeconds { get; init; }
}
