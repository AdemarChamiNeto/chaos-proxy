namespace Chaos.Domain;

/// <summary>
/// Padrão de rota simples, sem regex (regex vinda de fora pode travar o servidor, ReDoS):
/// <c>/api/tickets</c> casa só ela; <c>/api/tickets/*</c> casa um segmento a mais;
/// <c>/api/**</c> casa tudo que começa com /api. Maiúsculas e minúsculas são iguais.
/// </summary>
public sealed class PathPattern
{
    public const int MaxSegments = 20;

    private readonly string[] _segments;

    private PathPattern(string[] segments) => _segments = segments;

    public static bool TryParse(string? pattern, out PathPattern? result)
    {
        result = null;
        if (string.IsNullOrEmpty(pattern) || pattern[0] != '/') return false;
        var segments = Split(pattern);
        if (segments.Length > MaxSegments) return false;
        foreach (var s in segments)
        {
            // "*" e "**" só como segmento inteiro; nada de "tick*" ou "a**b"
            if (s.Contains('*') && s is not ("*" or "**")) return false;
            if (s.Length == 0) return false;
        }
        result = new PathPattern(segments);
        return true;
    }

    public static PathPattern Parse(string pattern) =>
        TryParse(pattern, out var p) ? p! : throw new FormatException($"Padrão de rota inválido: {pattern}");

    public bool Matches(string path)
    {
        var parts = Split(path);
        // memória dos pares (segmento, parte) que já falharam: sem ela, vários "**" seguidos
        // explodem em combinações (o mesmo problema de uma regex mal escrita)
        var failed = new bool[(_segments.Length + 1) * (parts.Length + 1)];
        return Match(0, 0, parts, failed);
    }

    private bool Match(int i, int j, string[] parts, bool[] failed)
    {
        var key = i * (parts.Length + 1) + j;
        if (failed[key]) return false;

        var ok = i == _segments.Length
            ? j == parts.Length
            : _segments[i] == "**"
                ? i == _segments.Length - 1 || Enumerable.Range(j, parts.Length - j + 1).Any(k => Match(i + 1, k, parts, failed))
                : j < parts.Length
                  && (_segments[i] == "*" || string.Equals(_segments[i], parts[j], StringComparison.OrdinalIgnoreCase))
                  && Match(i + 1, j + 1, parts, failed);

        if (!ok) failed[key] = true;
        return ok;
    }

    private static string[] Split(string path)
    {
        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0) path = path[..q];
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    public override string ToString() => "/" + string.Join('/', _segments);
}
