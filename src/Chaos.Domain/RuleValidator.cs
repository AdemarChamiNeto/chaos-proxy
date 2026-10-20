using System.Text.RegularExpressions;

namespace Chaos.Domain;

/// <summary>
/// Validação das regras que chegam pela API de controle. Devolve os erros por campo
/// (<c>rules[0].delayMaxMs</c>), no formato que a API responde em 422.
/// </summary>
public static partial class RuleValidator
{
    public const int MaxRules = 20;
    public const int MaxDelayMs = 30_000;
    public const int MaxPathLength = 200;

    private static readonly HashSet<string> Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex NamePattern();

    public static Dictionary<string, string> Validate(IReadOnlyList<FaultRule> rules)
    {
        var errors = new Dictionary<string, string>();
        if (rules.Count > MaxRules)
        {
            errors["rules"] = $"No máximo {MaxRules} regras";
            return errors;
        }

        var names = new HashSet<string>();
        for (var i = 0; i < rules.Count; i++)
        {
            var r = rules[i];
            var p = $"rules[{i}]";
            if (r is null)
            {
                errors[p] = "Regra vazia";
                continue;
            }

            if (r.Name is null || !NamePattern().IsMatch(r.Name)) errors[$"{p}.name"] = "Use de 1 a 40 caracteres: letras minúsculas, números e hífen";
            else if (!names.Add(r.Name)) errors[$"{p}.name"] = "Nome repetido";

            if (!Enum.IsDefined(r.Kind)) errors[$"{p}.kind"] = "Tipo desconhecido";

            if (r.Path is null || r.Path.Length > MaxPathLength || !PathPattern.TryParse(r.Path, out _))
                errors[$"{p}.path"] = $"Comece com / e use segmentos, * ou ** (até {MaxPathLength} caracteres)";

            if (r.Method is not null && !Methods.Contains(r.Method)) errors[$"{p}.method"] = "Método HTTP inválido (use maiúsculas, ex.: GET)";

            if (double.IsNaN(r.Probability) || r.Probability is < 0 or > 1) errors[$"{p}.probability"] = "Entre 0 e 1";

            switch (r.Kind)
            {
                case FaultKind.Latency:
                    if (r.DelayMinMs < 0 || r.DelayMaxMs <= 0 || r.DelayMinMs > r.DelayMaxMs || r.DelayMaxMs > MaxDelayMs)
                        errors[$"{p}.delayMaxMs"] = $"Use 0 ≤ delayMinMs ≤ delayMaxMs ≤ {MaxDelayMs}, com delayMaxMs > 0";
                    break;
                case FaultKind.Timeout:
                    if (r.HoldMs is <= 0 or > MaxDelayMs) errors[$"{p}.holdMs"] = $"Entre 1 e {MaxDelayMs}";
                    break;
                case FaultKind.Error:
                    if (r.Status is < 400 or > 599 || r.Status == 429) errors[$"{p}.status"] = "Entre 400 e 599 (para 429 use o tipo RateLimit)";
                    break;
                case FaultKind.HtmlError:
                    if (r.Status is < 500 or > 599) errors[$"{p}.status"] = "Entre 500 e 599";
                    break;
                case FaultKind.RateLimit:
                    if (r.Limit is < 1 or > 10_000) errors[$"{p}.limit"] = "Entre 1 e 10000";
                    if (r.WindowSeconds is < 1 or > 3600) errors[$"{p}.windowSeconds"] = "Entre 1 e 3600";
                    break;
                case FaultKind.Drop:
                default:
                    break;
            }
        }
        return errors;
    }
}
