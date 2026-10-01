# Déploiement — ON.VOYAGE

Sources : cahier des charges §9.7, §21, NF-05, NF-06 ; fichiers sous `deploy/` et `.github/workflows/`. Ce document décrit le **staging**. La production (T-010) réutilise la même mécanique, voir la dernière section.

## Environnements

| Environnement | Hébergement | Déploiement | Fichiers |
| --- | --- | --- | --- |
| `development` | poste du développeur | `dotnet run --project src/Aspire/OnVoyage.AppHost` (Aspire, PostGIS en conteneur, e-mail de connexion écrit dans le journal) | `AppHost.cs` |
| `staging` | un VPS européen, Docker Compose | automatique à chaque fusion sur `main` qui touche `src/`, `prompts/` ou `deploy/` (workflow `deploy-staging.yml`), ou à la main | `deploy/staging/` |
| `production` | un VPS européen | manuel, depuis une étiquette `vX.Y.Z` (T-010, hors périmètre ici) | à venir |

## Topologie du staging

```
Internet ──443──▶ Caddy (HTTPS automatique) ──┬─ /api/*, /media/*, /health ─▶ gateway ─▶ platform-api, catalog-api, discovery-api, factory-api
                                              ├─ /*                         ─▶ web-pwa (fichiers statiques)
                                              └─ admin.<domaine>            ─▶ web-admin (Blazor serveur)
factory-worker ──(volume media)──▶ catalog-api (lecture seule, sert /media)
tous les services ──▶ postgres (PostGIS 16, une base, un schéma par service)      backup ──▶ volume backups
```

- Seul Caddy publie des ports (80, 443). PostgreSQL et les services restent sur le réseau Compose `onvoyage-staging`.
- Le Gateway est l'unique point d'entrée de l'API (ADR-0006). Il lit `X-Forwarded-For` posé par Caddy (`Gateway__TrustForwardedHeaders=true`) pour la limitation de débit par IP.
- La PWA est servie sur la même origine que l'API (`Gateway:BaseAddress` vide) : aucune configuration CORS n'est nécessaire.
- Les journaux d'accès de Caddy suppriment la requête (`lat`, `lon`, `bbox`…), l'en-tête `Authorization`, les cookies, et tronquent l'IP (/24, /48) : §16.2 « journaux serveur ».
- Le volume `media` est partagé : `factory-worker` y écrit l'audio, `catalog-api` le sert sous `/media` (variables `Factory__MediaDirectory` et `Media__RootPath`, comme dans `AppHost.cs`). Il passera sur le stockage objet UE ensuite.

## Images

| Image | Dockerfile | Notes |
| --- | --- | --- |
| `platform-api`, `catalog-api`, `discovery-api`, `factory-api`, `gateway`, `web-admin` | `deploy/docker/service.Dockerfile` (arguments `PROJECT`, `ASSEMBLY`) | build en deux étapes (SDK 10 puis `aspnet:10.0`), utilisateur non root (`APP_UID`), port 8080, `HEALTHCHECK` sur `/alive` |
| `factory-worker` | idem, avec `RUNTIME_PACKAGES="curl osm2pgsql ffmpeg"` | `osm2pgsql` (import OSM, ADR-0004) et `ffmpeg` (normalisation audio) sont lus dans le `PATH` |
| `web-pwa` | `deploy/docker/pwa.Dockerfile` | `dotnet publish` de la PWA, servie par Caddy (utilisateur 10001, port 8080) ; `PWA_MAP_TILES_URL` est écrit dans `appsettings.json` au démarrage |
| `backup` | `deploy/staging/backup/Dockerfile` | `pg_dump`, `pg_basebackup`, `age` ; utilisateur 999 |

Registre : GitHub Container Registry, `ghcr.io/<propriétaire en minuscules>/onvoyage/<nom>:<sha du commit>` (et `:staging`). Les services à venir (Insights, Creators, Web.Public) s'ajoutent en copiant un bloc de `docker-compose.yml` (déjà présent en commentaire) et une ligne de la matrice de `deploy-staging.yml`.

Construire à la main : `docker compose -f deploy/staging/docker-compose.yml build platform-api`.

## Variables d'environnement

`deploy/staging/docker-compose.yml` reproduit `src/Aspire/OnVoyage.AppHost/AppHost.cs` en mode publication. La liste exhaustive des clés lues par le code est dans [CONFIGURATION.md](CONFIGURATION.md).

| Variable Compose (`.env`) | Clé .NET | Service |
| --- | --- | --- |
| `JWT_SECRET` | `Auth__JwtSecret` (32 octets minimum) | gateway, platform, catalog, discovery, factory-api |
| `POSTGRES_PASSWORD`, `POSTGRES_USER`, `POSTGRES_DB` | `ConnectionStrings__onvoyage` | tous les services avec base |
| `RESEND_API_KEY`, `EMAIL_FROM` | `Email__Provider=resend`, `Email__Resend__ApiKey`, `Email__From` | platform |
| `BOOTSTRAP_ADMIN_EMAIL` | `Auth__BootstrapAdminEmails__0` | platform |
| `OPENAI_API_KEY`, `FACTORY_LLM_*_MODEL`, `FACTORY_LLM_PROVIDER` | `OpenAI__ApiKey`, `Factory__Llm__{Extractor,Writer,Verifier,Classifier}Model`, `Factory__Llm__Provider` | factory-worker **et factory-api** |
| `MIGRATE_ON_START` | `Platform__Migrate`, `Catalog__Migrate`, `Discovery__Migrate`, `Factory__Migrate` | chaque service qui a un schéma |
| `CATALOG_SEED_DEMO_DATA` | `Catalog__SeedDemoData` | catalog |
| `STAGING_DOMAIN` | `Media__PublicBaseUrl`, `Admin__MediaBaseUrl`, `Cors__AllowedOrigins__0` | catalog, discovery, web-admin, gateway |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | idem | tous (voir [runbooks/observability.md](runbooks/observability.md)) |

Écart constaté avec `AppHost.cs` : en mode publication, `factory-api` n'y reçoit ni `OpenAI__ApiKey` ni les modèles, alors que `AddFactoryInfrastructure` les exige au démarrage quand `Factory:Llm:Provider` vaut `openai` (valeur par défaut). Le compose les fournit aux deux hôtes Factory ; `AppHost.cs` devrait faire de même.

## Secrets et variables GitHub (par nom)

Environnement GitHub `staging` (Settings → Environments). **Aucune valeur n'est écrite dans le dépôt.** Les valeurs ne doivent contenir ni apostrophe, ni espace, ni `;`, ni `$` ; générer les secrets avec `openssl rand -hex 32`.

| Secret | Contenu |
| --- | --- |
| `STAGING_SSH_HOST` | nom DNS ou IP du VPS |
| `STAGING_SSH_USER` | compte de déploiement (membre du groupe `docker`, aucun sudo) |
| `STAGING_SSH_PRIVATE_KEY` | clé privée SSH dédiée au déploiement (la publique est dans `authorized_keys` du VPS) |
| `STAGING_SSH_KNOWN_HOSTS` | sortie de `ssh-keyscan -p <port> <hôte>` relevée depuis un poste de confiance (empreinte épinglée) |
| `STAGING_POSTGRES_PASSWORD` | mot de passe PostgreSQL (hexadécimal, pas de `;`) |
| `STAGING_JWT_SECRET` | clé HS256 partagée |
| `STAGING_BOOTSTRAP_ADMIN_EMAIL` | adresse qui reçoit le rôle `admin` à la connexion |
| `STAGING_RESEND_API_KEY` | clé API Resend (domaine d'envoi vérifié) |
| `STAGING_OPENAI_API_KEY` | clé de projet OpenAI |
| `STAGING_GRAFANA_ADMIN_PASSWORD` | mot de passe Grafana (si la pile d'observabilité est activée) |

| Variable (non secrète) | Contenu |
| --- | --- |
| `STAGING_DOMAIN` | ex. `staging.on.voyage` (et `admin.staging.on.voyage` pointent vers le VPS) |
| `STAGING_ACME_EMAIL` | contact de l'autorité de certification |
| `STAGING_EMAIL_FROM` | expéditeur des codes (facultatif) |
| `STAGING_LLM_PROVIDER` | `openai` (défaut) ou `disabled` |
| `STAGING_LLM_EXTRACTOR_MODEL`, `STAGING_LLM_WRITER_MODEL`, `STAGING_LLM_VERIFIER_MODEL`, `STAGING_LLM_CLASSIFIER_MODEL` | identifiants de modèles |
| `STAGING_BACKUP_AGE_RECIPIENT` | clé **publique** age (`age1…`) pour chiffrer les sauvegardes |
| `STAGING_MAP_TILES_URL` | URL du fichier `.pmtiles` (facultatif) |
| `STAGING_DEPLOY_PATH`, `STAGING_SSH_PORT` | défauts `/opt/onvoyage` et `22` |
| `STAGING_OBSERVABILITY` | `true` pour démarrer aussi la pile d'observabilité |
| `STAGING_OTLP_ENDPOINT` | `http://otel-collector:4317` une fois la pile démarrée |

Le jeton du registre est celui du workflow (`GITHUB_TOKEN`, `packages: write`) ; il est passé au VPS par l'entrée standard de `docker login` pour la durée du déploiement, puis `docker logout`.

## Préparer le VPS (une fois, H-003)

1. VPS européen (≥ 4 vCPU, 16 Go), Docker Engine et le plugin Compose, pare-feu : 22, 80, 443 (et 443/udp) seulement.
2. Enregistrements DNS `A`/`AAAA` pour `<domaine>` et `admin.<domaine>`.
3. Compte `deploy` dans le groupe `docker`, clé SSH dédiée ; relever l'empreinte du serveur pour `STAGING_SSH_KNOWN_HOSTS`.
4. Générer la paire age **hors du serveur** : `age-keygen -o onvoyage-backup.key`. Publier la ligne `# public key: age1…` dans `STAGING_BACKUP_AGE_RECIPIENT` ; ranger la clé privée dans le gestionnaire de secrets du propriétaire (voir [runbooks/restore.md](runbooks/restore.md)).
5. Renseigner les secrets et variables ci-dessus, puis lancer le workflow `deploy-staging` (Run workflow).

## Déroulement d'un déploiement

1. `images` : une image par service, poussée sur GHCR avec le SHA du commit (cache de couches GitHub).
2. `deploy` (SSH, hôte épinglé) :
   1. écriture de `.env` depuis les secrets (droits 600) et envoi de `deploy/staging/` ;
   2. `docker compose pull` ;
   3. `up -d --wait postgres` puis une sauvegarde logique de sécurité (`backup.sh dump`, ignorée au tout premier déploiement) ;
   4. **migrations** : `up -d --wait platform-api catalog-api discovery-api factory-worker factory-api`. Chaque hôte applique ses migrations EF Core avant d'accepter des requêtes (réglages `Platform:Migrate`, `Catalog:Migrate`, `Discovery:Migrate`, `Factory:Migrate`, vrais par défaut) : un hôte « healthy » signifie un schéma migré ;
   5. `up -d --wait --remove-orphans` pour le reste (Gateway, PWA, admin, Caddy, sauvegardes) ;
   6. test de fumée `deploy/staging/smoke-test.sh https://<domaine>` : `/health` du Gateway, route publique `GET /api/platform/v1/config` (Gateway → Platform → base), session anonyme puis `GET /api/catalog/v1/destinations/marseille` avec le jeton (200 ou 404 ; 401 sans jeton), PWA, back-office.
3. Si un hôte ne devient pas sain, le workflow échoue et affiche les dernières lignes des journaux.

Les migrations doivent rester **rétrocompatibles d'une version** (§21) : les anciens conteneurs sont remplacés service par service, sans fenêtre de maintenance, mais il n'y a pas de bascule bleu/vert.

**Retour arrière** : lancer le workflow à la main avec `image_tag` = un SHA déjà publié (aucune construction). Une migration déjà appliquée n'est pas défaite ; en cas de dommage, restaurer la sauvegarde de sécurité ([runbooks/restore.md](runbooks/restore.md)).

## Sauvegardes et observabilité

- Sauvegardes : service `backup` (dump chiffré quotidien, base backup hebdomadaire, WAL chiffrés, rétention 30 jours) et test de restauration mensuel en CI. Procédure : [runbooks/restore.md](runbooks/restore.md).
- Observabilité : `deploy/staging/observability/` (collecteur OTLP, Prometheus, Loki, Tempo, Grafana). Procédure : [runbooks/observability.md](runbooks/observability.md).
- Les sauvegardes locales ne protègent pas de la perte du VPS : copier `/backups` (déjà chiffré) vers un stockage UE distinct reste à configurer (H-003).

## Ce que la production (T-010) réutilisera

- Les Dockerfiles et les images (même artefact, promu par étiquette `vX.Y.Z`).
- `docker-compose.yml`, `Caddyfile`, `postgres/`, `backup/`, `observability/` : même structure avec un `.env` de production, un domaine propre, le retrait de `X-Robots-Tag: noindex` et de `CATALOG_SEED_DEMO_DATA`.
- `smoke-test.sh` et la procédure de retour arrière.
- À écrire pour T-010 : workflow `deploy-production.yml` déclenché par étiquette, secrets `PRODUCTION_*`, approbation manuelle de l'environnement GitHub, politique de sauvegarde et d'alerte propre, stockage objet et CDN.

## Limites connues

- Aucune de ces briques n'a été exécutée sur un vrai VPS dans cette tranche : voir le compte rendu de vérification de la PR (compose validé par `docker compose config`, scripts de sauvegarde exécutés contre un PostgreSQL local ; images, Caddy, pile d'observabilité et workflow de déploiement non exécutés).
- Un seul VPS : la disponibilité de 99,5 % (NF-05) dépend de lui ; la sonde de santé et l'alerte `MonthlyAvailabilityBelowTarget` servent à la mesurer.
