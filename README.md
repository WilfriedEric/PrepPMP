# PMP Study

Application web personnelle pour préparer le PMP (format 2026) : lire le PMBOK 8 page par page avec des notes, s'entraîner en temps limité, passer des examens blancs, tenir le carnet d'erreurs et produire le bilan hebdomadaire.

Blazor Web App (.NET 10, rendu interactif serveur), MudBlazor, EF Core (SQLite en local, Azure SQL en production), ASP.NET Core Identity avec double authentification, PDF.js pour le lecteur.

## Démarrer en local

```bash
dotnet run --project src/PmpStudy.Web
```

1. Ouvrir l'URL affichée, créer le compte (l'inscription se ferme après le premier compte), puis activer la double authentification depuis le bandeau.
2. **Import et réglages** : charger le PDF du PMBOK (les signets sont lus pour créer les chapitres), importer le plan de révision (`docs/plan-exemple.json` montre le format), régler la date d'examen, les simulations et le décalage de pages.
3. La banque de questions de départ est chargée automatiquement au premier démarrage.

La base SQLite et le PDF vont dans `src/PmpStudy.Web/App_Data/` (ignoré par git).

## Ce qui est livré

| Module | Route | Lot |
|---|---|---|
| Aujourd'hui : plan du jour, objectif de questions, semaine, compte à rebours, erreurs dues | `/` | 1 |
| Lecteur PDF : page du jour, chapitres, reprise, temps de lecture | `/cours/{page}` | 1 |
| Notes Markdown par page, chapitre et tâche ECO ; recherche ; export | `/notes` | 1 |
| Entraînement chronométré (filtres domaine, tâche, approche, chapitre, source) | `/entrainement`, `/session/{id}` | 1 |
| Carnet d'erreurs : alimentation automatique, saisie externe, répétition espacée, rejeu | `/erreurs` | 1 |
| Examen blanc : 180 questions, 3 sections, pauses de 10 min, revue, sections verrouillées | `/examens`, `/examen/{id}` | 2 |
| Bilan : scores par domaine, tâche et approche ; examens ; heures ; Markdown du dimanche | `/bilan` | 2 |
| Import du plan, des chapitres et des questions ; réglages ; export complet | `/admin/import` | 1 |

Le lot 3 (surlignage PDF, appariement, listes déroulantes, zones cliquables, hors ligne) reste à faire.

## Règles principales

- **Le serveur fait foi pour le temps** : échéance fixée à l'ouverture, réponses enregistrées une par une, clôture à l'échéance même navigateur fermé (service d'arrière-plan). Les questions sans réponse comptent comme fausses.
- **Examen** : tirage proportionnel à l'ECO (59 Personnes / 74 Processus / 47 Environnement d'affaires, environ 40 % prédictif), questions les moins vues d'abord. Une pause ne compte pas jusqu'à 10 min ; au-delà, le dépassement est déduit du temps restant.
- **Carnet d'erreurs** : une entrée par question, créée pour toute réponse fausse ou marquée « doute » ; revue à J+1, J+3, J+7, J+14 ; une erreur remet à J+1 ; deux bonnes réponses de suite passent en « maîtrisée ». « Incomplète » tant que « pourquoi » et « piège » sont vides.
- **Niveaux internes** : ≥ 80 % au-dessus de la cible, 70–79 % cible, 60–69 % sous la cible, < 60 % à renforcer.

## Banque de questions

Format d'import (tableau JSON, voir aussi `/admin/import`) :

```json
{
  "id": "mon-lot-001",
  "type": "single",
  "ecoTask": "PE-2",
  "approach": "agile",
  "chapter": "2.6",
  "stem": "…",
  "options": [ { "text": "…", "correct": false }, { "text": "…", "correct": true } ],
  "explanation": "…",
  "source": "claude"
}
```

- `type` : `single` ou `multiple`. `approach` : `predictive`, `agile`, `hybrid`. Codes ECO : `PE-1`…`PE-8`, `PR-1`…`PR-10`, `EA-1`…`EA-8`.
- Avec un `id`, une réimportation met la question à jour au lieu de la dupliquer.
- Banque embarquée (`src/PmpStudy.Infrastructure/Seed/`) : 50 questions du test prédictif et 59 de l'examen blanc chapitres 1 à 12. Le document de l'examen blanc n'avait pas de corrigé : les réponses et explications ont été rédigées par Claude et sont signalées comme telles. La question 57 de ce document est écartée (énoncé et options ne correspondent pas).

## API

Toutes les routes exigent la connexion : `GET /api/plan/today`, `GET /api/course/pdf` (requêtes Range), `GET|POST|PUT|DELETE /api/notes`, `POST /api/sessions`, `POST /api/sessions/{id}/answers`, `POST /api/sessions/{id}/sections/{n}/break`, `POST /api/sessions/{id}/finish`, `GET /api/errors?due=today`, `POST /api/questions/import`, `POST /api/plan/import`, `GET /api/reports/weekly/{semaine}.md`, `GET /api/export`.

## Configuration et Azure

| Clé | Local (défaut) | Production |
|---|---|---|
| `Database:Provider` | `Sqlite` | `SqlServer` |
| `ConnectionStrings:DefaultConnection` | `DataSource=App_Data/pmpstudy.db` | chaîne Azure SQL (Key Vault) |
| `Storage:Provider` | `Local` | `Blob` |
| `ConnectionStrings:Blob`, `Storage:Container` | — | compte de stockage, conteneur privé |
| `Study:TimeZone` | `Europe/Paris` | à ajuster à ton fuseau |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | — | active Application Insights |

Les migrations existent pour les deux fournisseurs (`Migrations/Sqlite`, `Migrations/SqlServer`) et s'appliquent au démarrage. Pour en ajouter :

```bash
dotnet tool restore
cd src/PmpStudy.Infrastructure
dotnet ef migrations add <Nom> --context SqliteAppDbContext --output-dir Migrations/Sqlite
dotnet ef migrations add <Nom> --context SqlServerAppDbContext --output-dir Migrations/SqlServer
```

Déploiement : `.github/workflows/deploy-azure.yml` (manuel) publie vers App Service Linux avec une connexion OIDC (`AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` en secrets, `AZURE_WEBAPP_NAME` en variable). Les ressources Azure restent à créer, et le coût mensuel à valider avec le calculateur de prix avant.

## Tests

```bash
dotnet test
```

Règles (répétition espacée, chronomètre et pauses, tirage d'examen, scoring) et parcours complets sur SQLite en mémoire avec une horloge simulée.
