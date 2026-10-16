# PMP Study

Application web personnelle pour préparer le PMP (format 2026) : lire le PMBOK 8 page par page avec des notes, s'entraîner en temps limité, passer des examens blancs, tenir le carnet d'erreurs et produire le bilan hebdomadaire. Installable sur téléphone (PWA), avec un rappel quotidien.

Blazor Web App (.NET 10, rendu interactif serveur), MudBlazor, EF Core sur **SQL Server**, ASP.NET Core Identity (identifiant et mot de passe), PDF.js pour le lecteur.

## Tout est dans la base

La seule chose à configurer hors de la base est la **chaîne de connexion SQL Server**. Tout le reste y est stocké :

| Donnée | Table |
|---|---|
| Compte et mot de passe (haché) | `AspNetUsers` |
| Questions, options, scénarios | `Questions`, `QuestionOptions`, `Scenarios` |
| Réglages (date d'examen, simulations, heures par semaine, décalage de pages, fuseau horaire, rappel quotidien, clés de notification) | `Settings` |
| PDF du cours | `CourseFiles` |
| Plan, chapitres, notes, sessions, carnet d'erreurs, lecture | `PlanDays`, `Chapters`, `Notes`, `Sessions`, `ErrorEntries`, `ReadingLogs`... |
| Clés de chiffrement des cookies de connexion (les sessions survivent aux redémarrages) | `DataProtectionKeys` |
| Appareils abonnés aux rappels | `PushDevices` |

La banque de questions ne fait plus partie du code : elle s'importe depuis **Réglages et imports → Questions** (fichiers JSON, format plus bas).

## Hébergement mutualisé InterServer (Windows, IIS, Plesk)

1. **Base SQL Server** : créer la base et un utilisateur dans le panneau d'hébergement. Deux options pour les tables :
   - laisser l'application les créer au premier démarrage (l'utilisateur SQL doit avoir le droit de créer des tables, rôle `db_owner` sur sa base) ;
   - ou exécuter `deploy/creation-base-sqlserver.sql` dans le gestionnaire SQL (script idempotent, relançable sans risque). Exécuter le fichier **en entier** (il contient des séparateurs `GO`), sur une base vide ou déjà créée par ce script.
2. **Publier** depuis un poste de développement :
   ```bash
   dotnet publish src/PmpStudy.Web -p:PublishProfile=InterServer
   ```
   Le dossier `publish/interserver` contient l'application **autonome** (le runtime .NET 10 est inclus : rien à installer côté serveur) et son `web.config` IIS (mode hors processus, environnement `Production`).
3. **Chaîne de connexion** : copier `deploy/appsettings.Production.exemple.json` en `appsettings.Production.json` à côté de `PmpStudy.Web.exe`, puis renseigner serveur, base, utilisateur et mot de passe. Ce fichier n'est jamais versionné. Variante : variable d'environnement `ConnectionStrings__DefaultConnection` dans le `web.config`.
4. **Envoyer** le contenu de `publish/interserver` à la racine du site (FTP ou gestionnaire de fichiers Plesk), activer **HTTPS** (Let's Encrypt dans Plesk) et ouvrir le site : la première visite propose de créer le compte.
5. **Garder l'application éveillée** : sur un mutualisé, IIS arrête l'application après une vingtaine de minutes sans visite, ce qui suspend le rappel quotidien. Configurer un service de surveillance gratuit (UptimeRobot, cron-job.org…) qui appelle `https://ton-site/health` toutes les 5 minutes.

À savoir :
- **WebSockets** : si le serveur ne les active pas, Blazor bascule tout seul sur une connexion de secours (long polling), un peu moins réactive mais fonctionnelle.
- **Mise à jour** : republier puis remplacer les fichiers. Pendant la copie, déposer un fichier `app_offline.htm` à la racine (IIS l'affiche et libère les fichiers verrouillés), puis le supprimer.
- **Sauvegarde** : la base contient tout (y compris le PDF) ; une sauvegarde SQL Server suffit. L'export JSON complet reste disponible dans les réglages.

## Développement local

```bash
dotnet run --project src/PmpStudy.Web
```

En développement (`appsettings.Development.json`), l'application utilise une base **SQLite** locale (`src/PmpStudy.Web/App_Data/`, ignoré par git) pour ne pas dépendre du serveur. Pour pointer vers SQL Server en local, mettre `Database:Provider` à `SqlServer` et renseigner la chaîne.

## Structure

Un seul projet web, `src/PmpStudy.Web` :

| Dossier | Contenu |
|---|---|
| `Components/` | Pages et composants Blazor ; `Account/` : connexion, création du compte, mot de passe |
| `Api/` | API REST minimale (`/api/...`) |
| `Model/` | Entités, règles métier (chronomètre, scoring, tirage d'examen, répétition espacée, rappels), formats d'import |
| `Data/` | Contexte EF Core et migrations (SQL Server et SQLite) |
| `Services/` | Sessions, carnet d'erreurs, contenus et imports, bilan, notes, notifications, PDF en base, fuseau horaire |
| `wwwroot/` | Styles, polices Geist, JavaScript (lecteur PDF, notifications), PDF.js, manifeste et service worker |

Les tests sont dans `tests/PmpStudy.Tests` (projet séparé, non déployé). `deploy/` contient le script SQL et le modèle de configuration.

## Fonctions

| Module | Route |
|---|---|
| Aujourd'hui : plan du jour, objectif de questions, semaine, compte à rebours, erreurs dues | `/` |
| Lecteur PDF : page du jour, chapitres, reprise, temps de lecture, notes par page | `/cours/{page}` |
| Notes Markdown par page, chapitre et tâche ECO ; recherche ; export | `/notes` |
| Entraînement chronométré (filtres domaine, tâche, approche, chapitre, source) | `/entrainement`, `/session/{id}` |
| Examen blanc : 180 questions, 3 sections, pauses de 10 min, revue, sections verrouillées | `/examens`, `/examen/{id}` |
| Carnet d'erreurs : alimentation automatique, saisie externe, répétition espacée, rejeu | `/erreurs` |
| Bilan : scores par domaine, tâche et approche ; examens ; heures ; Markdown du dimanche | `/bilan` |
| Banque de questions : recherche, activation | `/admin/questions` |
| Réglages et imports : PDF, plan, chapitres, questions, réglages, rappel quotidien, sauvegarde | `/admin/import` |
| Mot de passe | `/Account/ChangePassword` |

### Règles principales

- **Le serveur fait foi pour le temps** : échéance fixée à l'ouverture, réponses enregistrées une par une, clôture à l'échéance même navigateur fermé. Les questions sans réponse comptent comme fausses.
- **Examen** : tirage proportionnel à l'ECO (59 Personnes / 74 Processus / 47 Environnement d'affaires, environ 40 % prédictif), questions les moins vues d'abord. Une pause ne compte pas jusqu'à 10 min ; au-delà, le dépassement est déduit du temps restant.
- **Carnet d'erreurs** : une entrée par question, créée pour toute réponse fausse ou marquée « doute » ; revue à J+1, J+3, J+7, J+14 ; une erreur remet à J+1 ; deux bonnes réponses de suite passent en « maîtrisée ».
- **Niveaux internes** : 80 % et plus au-dessus de la cible, 70 à 79 % cible, 60 à 69 % sous la cible, moins de 60 % à renforcer.
- **Compte unique** : la création de compte n'est possible que tant qu'aucun compte n'existe ; 5 essais de mot de passe ratés verrouillent le compte 15 minutes.

### Format d'import des questions

```json
[
  {
    "id": "mon-lot-001",
    "type": "single",
    "ecoTask": "PE-2",
    "approach": "agile",
    "stem": "…",
    "options": [ { "text": "…", "correct": false }, { "text": "…", "correct": true } ],
    "explanation": "…",
    "source": "claude"
  }
]
```

`type` : `single` ou `multiple`. `approach` : `predictive`, `agile`, `hybrid`. Codes ECO : `PE-1`…`PE-8`, `PR-1`…`PR-10`, `EA-1`…`EA-8`. Avec un `id`, une réimportation met la question à jour au lieu de la dupliquer.

## Application installable et rappels

- **Installation** : menu du navigateur → « Installer l'application » ; sur iPhone : Safari → Partager → « Sur l'écran d'accueil » (iOS 16.4 ou plus pour les notifications).
- **Rappel quotidien** : dans les réglages, choisir l'heure, puis « Activer sur cet appareil » sur chaque téléphone ou ordinateur. Web Push standard (VAPID, aes128gcm) ; clés générées au premier usage et gardées en base.

## Évolution du schéma

Les migrations s'appliquent au démarrage. Pour en ajouter (depuis `src/PmpStudy.Web`) :

```bash
dotnet tool restore
dotnet ef migrations add <Nom> --context SqlServerAppDbContext --output-dir Data/Migrations/SqlServer
dotnet ef migrations add <Nom> --context SqliteAppDbContext --output-dir Data/Migrations/Sqlite
dotnet ef migrations script --idempotent --context SqlServerAppDbContext -o ../../deploy/creation-base-sqlserver.sql
sed -i '1s/^\xEF\xBB\xBF//' ../../deploy/creation-base-sqlserver.sql   # retire le BOM, mal lu par certains outils SQL
```

## Tests

```bash
dotnet test
```
