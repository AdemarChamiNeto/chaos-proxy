namespace Chaos.Proxy;

/// <summary>
/// Variáveis de ambiente, validadas na subida: se algo estiver errado, o app não sobe e diz o
/// quê (nunca o valor, para não vazar segredo no log).
/// </summary>
public sealed record ChaosSettings(Uri? Upstream, string? AdminToken, int ProxyLimitPerMinute, bool TrustForwardedHeaders)
{
    public const int MinTokenLength = 32;

    public bool FakeUpstream => Upstream is null;

    public static ChaosSettings Load(IConfiguration config, IHostEnvironment env)
    {
        var problems = new List<string>();

        Uri? upstream = null;
        var rawUpstream = config["UPSTREAM_URL"];
        if (!string.IsNullOrWhiteSpace(rawUpstream))
        {
            if (!Uri.TryCreate(rawUpstream.TrimEnd('/'), UriKind.Absolute, out upstream) || upstream.Scheme is not ("http" or "https"))
                problems.Add("UPSTREAM_URL precisa ser uma URL http(s) absoluta, ex.: http://localhost:8080");
        }

        var token = config["CHAOS_ADMIN_TOKEN"];
        if (string.IsNullOrEmpty(token))
        {
            token = null;
            if (env.IsProduction()) problems.Add($"CHAOS_ADMIN_TOKEN é obrigatório em produção (mínimo {MinTokenLength} caracteres)");
        }
        else if (token.Length < MinTokenLength)
        {
            problems.Add($"CHAOS_ADMIN_TOKEN precisa de pelo menos {MinTokenLength} caracteres");
        }

        var limit = 600;
        var rawLimit = config["PROXY_RATE_LIMIT"];
        if (!string.IsNullOrWhiteSpace(rawLimit) && (!int.TryParse(rawLimit, out limit) || limit is < 1 or > 100_000))
            problems.Add("PROXY_RATE_LIMIT precisa ser um inteiro entre 1 e 100000 (requisições por minuto por cliente)");

        var trust = string.Equals(config["TRUST_FORWARDED_HEADERS"], "true", StringComparison.OrdinalIgnoreCase);

        if (problems.Count > 0)
            throw new InvalidOperationException("Configuração inválida:\n- " + string.Join("\n- ", problems));

        return new ChaosSettings(upstream, token, limit, trust);
    }
}
