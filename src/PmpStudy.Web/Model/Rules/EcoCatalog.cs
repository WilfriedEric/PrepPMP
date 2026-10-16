using PmpStudy.Web.Model.Entities;

namespace PmpStudy.Web.Model.Rules;

/// <summary>
/// Tâches de l'Examination Content Outline PMP 2026 (8 Personnes, 10 Processus,
/// 8 Environnement d'affaires). Intitulés traduits librement : ils restent modifiables en base.
/// </summary>
public static class EcoCatalog
{
    public static readonly IReadOnlyList<EcoTask> Tasks =
    [
        new() { Code = "PE-1", Domain = Domain.People, Title = "Développer une vision commune" },
        new() { Code = "PE-2", Domain = Domain.People, Title = "Gérer les conflits" },
        new() { Code = "PE-3", Domain = Domain.People, Title = "Diriger l'équipe de projet" },
        new() { Code = "PE-4", Domain = Domain.People, Title = "Impliquer les parties prenantes" },
        new() { Code = "PE-5", Domain = Domain.People, Title = "Aligner les attentes des parties prenantes" },
        new() { Code = "PE-6", Domain = Domain.People, Title = "Gérer les attentes des parties prenantes" },
        new() { Code = "PE-7", Domain = Domain.People, Title = "Assurer le transfert de connaissances" },
        new() { Code = "PE-8", Domain = Domain.People, Title = "Planifier et gérer la communication" },

        new() { Code = "PR-1", Domain = Domain.Process, Title = "Élaborer un plan de management intégré et planifier la livraison" },
        new() { Code = "PR-2", Domain = Domain.Process, Title = "Définir et gérer le périmètre" },
        new() { Code = "PR-3", Domain = Domain.Process, Title = "Assurer une livraison axée sur la valeur" },
        new() { Code = "PR-4", Domain = Domain.Process, Title = "Planifier et gérer les ressources" },
        new() { Code = "PR-5", Domain = Domain.Process, Title = "Planifier et gérer les approvisionnements" },
        new() { Code = "PR-6", Domain = Domain.Process, Title = "Planifier et gérer les finances (budget et coûts)" },
        new() { Code = "PR-7", Domain = Domain.Process, Title = "Planifier et optimiser la qualité des livrables" },
        new() { Code = "PR-8", Domain = Domain.Process, Title = "Planifier et gérer l'échéancier" },
        new() { Code = "PR-9", Domain = Domain.Process, Title = "Évaluer l'état d'avancement du projet" },
        new() { Code = "PR-10", Domain = Domain.Process, Title = "Gérer la clôture du projet" },

        new() { Code = "EA-1", Domain = Domain.BusinessEnvironment, Title = "Définir et établir la gouvernance du projet" },
        new() { Code = "EA-2", Domain = Domain.BusinessEnvironment, Title = "Planifier et gérer la conformité" },
        new() { Code = "EA-3", Domain = Domain.BusinessEnvironment, Title = "Gérer et maîtriser les changements" },
        new() { Code = "EA-4", Domain = Domain.BusinessEnvironment, Title = "Lever les obstacles et gérer les problèmes" },
        new() { Code = "EA-5", Domain = Domain.BusinessEnvironment, Title = "Planifier et gérer les risques" },
        new() { Code = "EA-6", Domain = Domain.BusinessEnvironment, Title = "Favoriser l'amélioration continue" },
        new() { Code = "EA-7", Domain = Domain.BusinessEnvironment, Title = "Soutenir le changement organisationnel" },
        new() { Code = "EA-8", Domain = Domain.BusinessEnvironment, Title = "Évaluer les évolutions de l'environnement externe" },
    ];

    /// <summary>Tri naturel « PR-2 » avant « PR-10 ».</summary>
    public static string SortKey(string code)
    {
        var dash = code.IndexOf('-');
        return dash > 0 && int.TryParse(code[(dash + 1)..], out var n) ? $"{code[..dash]}-{n:D3}" : code;
    }
}
