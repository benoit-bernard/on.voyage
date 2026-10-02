# Runbook — toute la plateforme en local, sans Docker ni Aspire

`scripts/dev-local.sh` démarre PostgreSQL/PostGIS (celui de la machine) et chaque service en processus `dotnet run`, avec une configuration cohérente ; `scripts/smoke-local.sh` vérifie le parcours d'un voyageur à travers le Gateway. Aspire (`dotnet run --project src/Aspire/OnVoyage.AppHost`) reste la voie avec conteneurs.

## Prérequis
- .NET 10 SDK, `curl`, `jq`.
- PostgreSQL 16+ avec les extensions `postgis`, `pg_trgm`, `unaccent` installées (paquet `postgresql-NN-postgis-3`). Le script démarre le service s'il est arrêté (`service postgresql start`), crée le rôle `ov` (mot de passe `ov`), la base `onvoyage` et les extensions. Il lui faut un super-utilisateur : `ONVOYAGE_PG_SUPERUSER` / `ONVOYAGE_PG_SUPERPASSWORD` (défauts `postgres` / `pgtest`, comme les tests).
- `ffmpeg` (normalisation de l'audio) et `espeak-ng` (voix de développement, `apt install espeak-ng`). Sans `espeak-ng`, les histoires sont publiées sans audio ; sans `ffmpeg`, l'audio d'un lieu échoue (journal du worker) et l'import le reprend au prochain démarrage.
- `osm2pgsql` seulement pour l'import OSM réel (pas pour la démo).

## Utilisation
```
scripts/dev-local.sh up            # compile si besoin, démarre les 10 processus, importe le snapshot de Marseille
scripts/smoke-local.sh             # attend le catalogue (jusqu'à 10 min) puis déroule le parcours voyageur
scripts/dev-local.sh status
scripts/dev-local.sh logs factory-worker
scripts/dev-local.sh restart factory-worker   # après une recompilation d'un service
scripts/dev-local.sh down
scripts/dev-local.sh reset-db      # supprime la base onvoyage et le dossier média
```
Options de `up` : `--build` (force `dotnet build OnVoyage.slnx -m:1`), `--no-snapshot`.

| Service | Port | | Service | Port |
| --- | --- | --- | --- | --- |
| gateway | 5080 | | platform | 5101 |
| pwa | 5090 | | catalog | 5102 |
| web-public | 5110 | | discovery | 5103 |
| factory-worker (santé) | 5107 | | insights | 5104 |
| factory-api | 5106 | | creators | 5105 |

Fichiers : `.local/` (ignoré par git) — `logs/`, `pids/`, `media/` (audio), `exports/`, `factory/`.

## Configuration commune (variables d'environnement posées par le script)
`ConnectionStrings__onvoyage`, `Auth__JwtSecret` (clé de développement), `Exports__Directory`, `Media__RootPath` = `Factory__MediaDirectory`, `Media__PublicBaseUrl=http://localhost:5080/media` (l'audio est lu **par le Gateway**), `Email__Provider=log` (le code de connexion est écrit dans `.local/logs/platform.log`), `Catalog__SeedDemoData=false`, `Factory__Llm__Provider=offline`, `Factory__Snapshot__ImportOnStart=true`, `Cors__AllowedOrigins`, et les adresses `services__<nom>__http__0` que le Gateway utilise pour joindre les services (même mécanisme qu'Aspire ; passées par `env` car les noms contiennent un tiret). Chaque variable peut être surchargée avant l'appel : `Factory__Llm__Provider=disabled scripts/dev-local.sh up`.

## Ce que le test de fumée vérifie
Gateway → session anonyme → destination `marseille` (≥ 30 lieux) → lieux à 2 km du Vieux-Port → histoire d'un lieu (texte, `aiGenerated`, attributions, liens Wikipédia) → téléchargement réel du MP3 par l'adresse du Gateway (type `audio/mpeg`, requête `Range` en 206, balises ID3 avec `ffprobe`) → Discovery (`for-me`, `recommendations`) → site public `/fr/marseille` et PWA.

## Dépannage
- « le catalogue n'a que N lieux » : l'import dure environ 3 minutes (36 lieux × 5 parties audio). Suivre `scripts/dev-local.sh logs factory-worker` (ligne `Snapshot marseille v…`).
- Un service ne démarre pas : `.local/logs/<service>.log`. Le premier démarrage affiche une erreur EF « relation … __ef_migrations_history does not exist » : c'est la vérification avant la première migration, sans conséquence.
- Un port est pris : `P_GATEWAY=5081 scripts/dev-local.sh up` (la PWA lit le Gateway à `http://localhost:5080/` dans `wwwroot/appsettings.Development.json`).
- Après une mise à jour du snapshot : `scripts/dev-local.sh restart factory-worker` ; seuls les lieux et histoires modifiés sont republiés (nouvelle version).
