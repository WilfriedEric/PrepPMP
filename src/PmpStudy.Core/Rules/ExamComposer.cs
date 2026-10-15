using PmpStudy.Core.Entities;

namespace PmpStudy.Core.Rules;

public record ExamCandidate(int QuestionId, Domain? Domain, Approach Approach, int TimesUsedInExams);

/// <summary>
/// Tirage d'un examen blanc proportionnel à l'ECO 2026 : 33 % Personnes, 41 % Processus,
/// 26 % Environnement d'affaires (soit 59 / 74 / 47 sur 180), et environ 40 % prédictif.
/// Les questions les moins vues en examen passent en premier.
/// </summary>
public static class ExamComposer
{
    public static readonly IReadOnlyDictionary<Domain, double> DomainWeights = new Dictionary<Domain, double>
    {
        [Domain.People] = 0.33,
        [Domain.Process] = 0.41,
        [Domain.BusinessEnvironment] = 0.26,
    };

    public const double PredictiveShare = 0.40;

    /// <summary>Répartit <paramref name="total"/> selon les poids, méthode du plus fort reste.</summary>
    public static Dictionary<Domain, int> DomainTargets(int total)
    {
        var raw = DomainWeights.ToDictionary(kv => kv.Key, kv => kv.Value * total);
        var result = raw.ToDictionary(kv => kv.Key, kv => (int)Math.Floor(kv.Value));
        var missing = total - result.Values.Sum();
        foreach (var kv in raw.OrderByDescending(kv => kv.Value - Math.Floor(kv.Value)).ThenBy(kv => kv.Key).Take(missing))
            result[kv.Key]++;
        return result;
    }

    public static IReadOnlyList<int> Compose(IEnumerable<ExamCandidate> candidates, int total, Random random)
    {
        // Ordre de préférence : moins vues d'abord, puis hasard.
        var pool = candidates
            .Select(c => (c, key: random.Next()))
            .OrderBy(x => x.c.TimesUsedInExams).ThenBy(x => x.key)
            .Select(x => x.c)
            .ToList();
        total = Math.Min(total, pool.Count);
        var picked = new List<ExamCandidate>(total);
        var used = new HashSet<int>();

        void Take(IEnumerable<ExamCandidate> source, int count)
        {
            foreach (var c in source)
            {
                if (count <= 0) break;
                if (used.Add(c.QuestionId)) { picked.Add(c); count--; }
            }
        }

        foreach (var (domain, target) in DomainTargets(total))
        {
            var inDomain = pool.Where(c => c.Domain == domain).ToList();
            var predictiveTarget = (int)Math.Round(target * PredictiveShare);
            var before = picked.Count;
            Take(inDomain.Where(c => c.Approach == Approach.Predictive), predictiveTarget);
            Take(inDomain.Where(c => c.Approach != Approach.Predictive), target - (picked.Count - before));
            // Pas assez dans un des deux groupes : on complète avec l'autre.
            Take(inDomain, target - (picked.Count - before));
        }

        // Domaine en manque (ou questions sans tâche ECO) : on complète avec le reste.
        Take(pool, total - picked.Count);

        return picked.Select(c => (c.QuestionId, key: random.Next()))
            .OrderBy(x => x.key)
            .Select(x => x.QuestionId)
            .ToList();
    }
}
