# ON.VOYAGE

Guide de voyage audio géolocalisé qui apprend les goûts du voyageur et privilégie les lieux moins fréquentés. Première destination : Marseille. Les histoires sont rédigées à partir de sources vérifiées par un pipeline d'IA en tâche de fond, relues, puis lues par une voix de synthèse (toujours signalée). **Aucune donnée voyageur n'est transmise à un tiers, aucune position n'est stockée.**

Phase actuelle : **MVP-0** (prouver qu'un visiteur préfère « Pour vous » à une liste générique, à Marseille). Source de vérité : [`docs/CAHIER_DES_CHARGES.md`](docs/CAHIER_DES_CHARGES.md).

## Pile technique

.NET 10, Aspire, WolverineFx (messagerie et outbox PostgreSQL), ASP.NET Core Minimal APIs, EF Core 10 sur PostgreSQL + PostGIS, YARP (Gateway), MAUI Blazor Hybrid (Android, iOS) et PWA Blazor WebAssembly sur une bibliothèque de composants commune. Détail et diagrammes : [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Prérequis

- SDK .NET 10 (`global.json`) ;
- Docker (Aspire démarre PostgreSQL/PostGIS ; les tests d'intégration utilisent Testcontainers) ;
- facultatif : `osm2pgsql` et `ffmpeg` pour exécuter réellement le pipeline Factory ; les charges de travail MAUI pour l'application mobile ([`docs/MOBILE.md`](docs/MOBILE.md)).

## Démarrage en 5 commandes

```bash
git clone https://github.com/benoit-bernard/on.voyage.git && cd on.voyage
dotnet build OnVoyage.slnx                            # avertissements = erreurs
dotnet test --solution OnVoyage.slnx                  # unitaires, intégration, architecture, composants
dotnet format OnVoyage.slnx --verify-no-changes       # style
dotnet run --project src/Aspire/OnVoyage.AppHost      # lance tout, tableau de bord Aspire
```

Puis, en local :

| Adresse | Quoi |
| --- | --- |
| http://localhost:5080 | Gateway (point d'entrée de l'API) |
| http://localhost:5090 | PWA |
| http://localhost:5190 | Back-office (Blazor) |

- Le catalogue de Marseille (36 lieux, histoires, audio `espeak-ng` si disponible) vient du snapshot `data-pipeline/marseille/`, importé au démarrage du worker Factory (`Factory__Snapshot__ImportOnStart`). Sans Docker : `scripts/dev-local.sh up` puis `scripts/smoke-local.sh` ([docs/runbooks/dev-local.md](docs/runbooks/dev-local.md)).
- La connexion par e-mail n'envoie rien en local : le code à 6 chiffres est écrit dans le journal du service `platform-api` (ligne `DEVELOPMENT sign-in code`).
- Pour entrer dans le back-office, l'adresse utilisée doit recevoir le rôle `admin` : renseigner `Auth__BootstrapAdminEmails__0` pour `platform-api` (variable d'environnement du processus qui lance l'AppHost, que les services lancés héritent ; non vérifié dans cette tranche).
- Sans clé OpenAI, Factory tourne avec `Factory__Llm__Provider=offline` : adaptateurs déterministes et voix `espeak-ng` (`apt install espeak-ng`), sans réseau ; le contenu vient du snapshot. Le chemin réel (OpenAI, Wikipédia, plafond de coût) : [docs/runbooks/bootstrap-marseille.md](docs/runbooks/bootstrap-marseille.md).

## Structure

| Dossier | Contenu |
| --- | --- |
| `src/Services/{Platform,Catalog,Discovery,Factory}` | un service = `Domain`, `Application`, `Infrastructure`, `Api` (+ `Contracts`, + `Worker` pour Factory) |
| `src/Gateway` | proxy YARP : JWT, limitation de débit, blocage des robots d'IA |
| `src/Aspire` | `AppHost` (orchestration locale et modèle de publication) et `ServiceDefaults` (OpenTelemetry, santé, authentification, résilience) |
| `src/Shared` | `Messaging` (Wolverine), `Recommendation.Engine` (apprentissage, classement, planificateur), `Taxonomy` |
| `src/Web` | `UI.Components` (RCL partagée), `Web.Pwa`, `Web.Admin`, `Web.Studio` (espace créateur), `Web.Public` |
| `src/Mobile` | `App` (MAUI, hors `OnVoyage.slnx`), `App.Core`, `App.Infrastructure`, `App.LocalData` |
| `tests/` | projets de test (voir [`docs/TESTING.md`](docs/TESTING.md)) |
| `prompts/`, `data-pipeline/` | prompts versionnés de Factory ; taxonomie, style osm2pgsql, traces GPX synthétiques |
| `deploy/` | Dockerfiles, Docker Compose du staging, sauvegardes, observabilité |
| `.github/workflows/` | CI, déploiement du staging, build mobile, test de restauration |

## Documentation

| Document | Sujet |
| --- | --- |
| [`docs/CAHIER_DES_CHARGES.md`](docs/CAHIER_DES_CHARGES.md) | spécification, décisions, backlog |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | services, messagerie, flux de données, frontières de vie privée |
| [`docs/DATABASE.md`](docs/DATABASE.md) | schémas et tables |
| [`docs/API.md`](docs/API.md) | endpoints, routes du Gateway, politiques d'accès |
| [`docs/CONFIGURATION.md`](docs/CONFIGURATION.md) | tous les réglages lus par le code |
| [`docs/TESTING.md`](docs/TESTING.md) | stratégie et commandes de test |
| [`docs/CONTRIBUTING.md`](docs/CONTRIBUTING.md) | règles de contribution et définition de « terminé » |
| [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) | environnements, déploiement du staging |
| [`docs/runbooks/restore.md`](docs/runbooks/restore.md), [`docs/runbooks/observability.md`](docs/runbooks/observability.md) | sauvegardes et restauration ; supervision |
| [`docs/PRIVACY.md`](docs/PRIVACY.md) | registre des traitements |
| [`docs/MOBILE.md`](docs/MOBILE.md) | compiler et lancer l'app et la PWA |
| [`docs/stores/`](docs/stores/README.md) | brouillons des fiches App Store et Google Play |
| [`docs/adr/`](docs/adr) | décisions d'architecture ; [`docs/questions/`](docs/questions) : questions ouvertes |

Règles pour les agents et contributeurs : [`CLAUDE.md`](CLAUDE.md), [`.github/copilot-instructions.md`](.github/copilot-instructions.md).

## Licence et données

Les données cartographiques sont © contributeurs OpenStreetMap ; fond de carte Protomaps. Les sources et licences des histoires sont citées dans l'application. Le domaine du projet est conservé par le fichier `CNAME`.
