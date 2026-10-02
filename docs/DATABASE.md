# Base de données — ON.VOYAGE

Source : les `DbContext` et les instantanés de modèle EF Core des quatre services qui ont une base (`*DbContextModelSnapshot.cs`), relus au 2026-10-01. Les tableaux de colonnes de chaque table ci-dessous sont **générés à partir de ces instantanés** (types PostgreSQL, nullité, clés, index, contraintes). Insights, Creators, Billing et Ads n'ont pas encore de schéma dans le dépôt ; leurs tables s'ajouteront ici.

## Organisation

- **Une base PostgreSQL** (`onvoyage`, image `postgis/postgis:16-3.4`), **un schéma par service**, une `DbContext` par service. Aucun service ne lit le schéma d'un autre : ils échangent des événements d'intégration (voir [ARCHITECTURE.md](ARCHITECTURE.md)).
- Extensions utilisées : `postgis` (Catalog, Factory), `pg_trgm` (Catalog, Factory, Creators), `unaccent` (Catalog), `citext` (Creators). Les migrations les créent (`CREATE EXTENSION IF NOT EXISTS`) : le rôle de connexion doit pouvoir le faire. **pgvector n'est pas utilisé** (le vecteur d'intérêts est un `real[]`, ADR-0011).
- **Un seul rôle de connexion** est utilisé par tous les services (chaîne `ConnectionStrings:onvoyage`). Le rôle par service et par schéma exigé par SEC-07 n'est **pas encore en place** (en staging, l'utilisateur `POSTGRES_USER` est super-utilisateur).
- Les migrations sont appliquées **au démarrage de chaque hôte** (`Platform:Migrate`, `Catalog:Migrate`, `Discovery:Migrate`, `Factory:Migrate`, `Creators:Migrate`, vrais par défaut), avant qu'il accepte des requêtes. Elles doivent rester rétrocompatibles d'une version (§21).
- Identifiants : UUID v7 (`Guid.CreateVersion7()`), dates en `timestamptz` (UTC).
- Noms : tables et colonnes en `snake_case` minuscule.

### Tables techniques de Wolverine (pas de modèle EF)

Wolverine crée et met à jour lui-même (`AutoCreate.CreateOrUpdate`, `AutoProvision`) :

- dans le **schéma de chaque service** (`platform`, `catalog`, `discovery`, `factory`) : les tables de la boîte d'envoi et de la boîte de réception durables (outbox/inbox transactionnelles) et des lettres mortes ;
- dans le schéma partagé **`wolverine_queues`** : une file PostgreSQL par service consommateur (`platform`, `catalog`, `discovery`, `factory`).

Elles ne sont pas listées colonne par colonne car leur forme dépend de la version de Wolverine ; ne pas les modifier à la main. Un message qui échoue est réessayé avec des délais croissants puis déplacé en lettre morte, jamais perdu (`OnAnyException().MoveToErrorQueue()`).

### Schéma `factory_raw` (hors EF)

`osm2pgsql` y écrit, à chaque import, une table datée `factory_raw.osm_place_<AAAAMMJJhhmm>` (mode flex, `--drop`) ; Factory en reconstruit `factory.place`, puis ne garde que les **deux plus récentes**. Deux tables EF vivent aussi dans ce schéma (`wikidata_entity`, `wikipedia_pageviews`).

### Conservation

Les durées du §16.2 sont **configurées** dans la clé de configuration distante `retention` (`anonymous_inactive_months` 24, `analytics_raw_months` 13, `technical_events_days` 90, `server_logs_days` 30, `error_reports_months` 12, `impressions_days` 90, `export_link_hours` 24, `backup_days` 30). **Aucune tâche de purge n'existe encore dans le code** (le seul nettoyage est celui des tables OSM brutes). Tant que ce n'est pas fait, ces durées sont des engagements, pas des comportements : `discovery.impression`, les comptes anonymes inactifs, `factory.story_report` et `platform.otp_challenge` / `refresh_token` expirés s'accumulent. Les sauvegardes expirent après 30 jours ([runbooks/restore.md](runbooks/restore.md)).

## Données personnelles par schéma

| Schéma | Tables à données personnelles | Nature |
| --- | --- | --- |
| `platform` | `account`, `otp_challenge`, `refresh_token`, `consent` | e-mail (facultatif), identifiant voyageur, empreintes de codes et de jetons, consentements |
| `discovery` | `traveler`, `interest_vector`, `interaction`, `poi_rating`, `visit`, `impression` | profil de goûts et historique de lieux (donnée de localisation au sens du RGPD, §16.2), sans coordonnées |
| `factory` | `story_report` | identifiant voyageur et motif du signalement |
| `creators` | `follow`, `moderation_case` (référence du voyageur qui signale), `creator` (profil public d'un créateur, identifiant de compte, référence du consentement signé) | abonnements (jamais exposés), signalements, profil publié avec consentement |
| `catalog` | aucune | contenu éditorial |

Le registre officiel est [PRIVACY.md](PRIVACY.md). La suppression en cascade existe dans `discovery` (toutes les tables filles de `traveler` sont en `ON DELETE CASCADE`) ; l'orchestration de la suppression entre services (F-22, T-507) n'est pas encore écrite.

## Tables

### Service Platform — schéma `platform`

Contexte EF : `OnVoyage.Platform.Infrastructure/Persistence/PlatformDbContext.cs`. Migrations : `20261001070745_InitialPlatform`, `20261001073324_Identity`, `20261001112909_AdminAudit`.

#### `platform.account`

Un compte par voyageur : anonyme (e-mail nul) ou lié à une adresse vérifiée ; `roles` porte `admin`, `creator`… ; `replaced_by` renseigné = compte absorbé par un autre, qui n'est plus utilisable (ses jetons sont refusés).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `created_at` | `timestamp with time zone` | non |  |
| `email` | `text` | oui |  |
| `email_verified_at` | `timestamp with time zone` | oui |  |
| `last_active_at` | `timestamp with time zone` | non |  |
| `replaced_by` | `uuid` | oui |  |
| `roles` | `text[]` | non |  |

- Index (`email`) : unique, partiel `email is not null`
- Index (`last_active_at`)

#### `platform.admin_audit`

Journal consultable des actions d'administration de tous les services (SEC-10), alimenté par `AdminActionRecordedV1`.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `event_id` | `uuid` | non | clé primaire |
| `action` | `text` | non |  |
| `actor` | `text` | non |  |
| `at` | `timestamp with time zone` | non |  |
| `service` | `text` | non |  |
| `status` | `integer` | non |  |
| `summary` | `text` | oui |  |
| `target` | `text` | non |  |

- Index (`at`)
- Index (`service`, `at`)

#### `platform.consent`

Consentements par voyageur et par type (`analytics`, `ads_personalization`), avec la version du texte accepté. Absence de ligne = refusé.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `traveler_id` | `uuid` | non | clé primaire (composite) |
| `kind` | `text` | non | clé primaire (composite) |
| `granted` | `boolean` | non |  |
| `text_version` | `text` | non |  |
| `updated_at` | `timestamp with time zone` | non |  |

- Clé primaire : (`traveler_id`, `kind`)
- Contrainte `ck_consent_kind` : `kind in ('analytics', 'ads_personalization')`

#### `platform.feature_flag`

Drapeaux de fonctionnalité (§18) : pourcentage de déploiement, plateformes, version minimale.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `name` | `text` | non | clé primaire |
| `enabled` | `boolean` | non |  |
| `min_app_version` | `text` | oui |  |
| `platforms` | `text[]` | non |  |
| `rollout_percent` | `smallint` | non |  |

- Contrainte `ck_feature_flag_rollout` : `rollout_percent between 0 and 100`

#### `platform.otp_challenge`

Codes de connexion à 6 chiffres : seule l'empreinte HMAC (`code_hash`) est stockée, avec adresse, expiration et nombre d'essais.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `attempts` | `integer` | non |  |
| `code_hash` | `text` | non |  |
| `consumed_at` | `timestamp with time zone` | oui |  |
| `created_at` | `timestamp with time zone` | non |  |
| `email` | `text` | non |  |
| `expires_at` | `timestamp with time zone` | non |  |

- Index (`email`, `created_at`)

#### `platform.refresh_token`

Jetons de rafraîchissement : empreinte (`token_hash`), famille pour la rotation, dates d'usage et de révocation.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `account_id` | `uuid` | non |  |
| `created_at` | `timestamp with time zone` | non |  |
| `expires_at` | `timestamp with time zone` | non |  |
| `family_id` | `uuid` | non |  |
| `revoked_at` | `timestamp with time zone` | oui |  |
| `token_hash` | `text` | non |  |
| `used_at` | `timestamp with time zone` | oui |  |

- Index (`account_id`)
- Index (`family_id`)
- Index (`token_hash`) : unique

#### `platform.remote_config`

Configuration distante versionnée (annexe E, §18), une ligne par clé de premier niveau.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `key` | `text` | non | clé primaire |
| `updated_at` | `timestamp with time zone` | non |  |
| `updated_by` | `text` | non |  |
| `value` | `jsonb` | non |  |
| `version` | `integer` | non |  |

#### `platform.remote_config_history`

Historique des versions de chaque clé de configuration.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `key` | `text` | non | clé primaire (composite) |
| `version` | `integer` | non | clé primaire (composite) |
| `updated_at` | `timestamp with time zone` | non |  |
| `updated_by` | `text` | non |  |
| `value` | `jsonb` | non |  |

- Clé primaire : (`key`, `version`)

### Service Catalog — schéma `catalog`

Contexte EF : `OnVoyage.Catalog.Infrastructure/Persistence/CatalogDbContext.cs`. Migrations : `20261001062605_InitialCatalog`, `20261001070737_ConfigSnapshot`, `20261001093659_StoryAudioAndSources`, `20261001121054_ExternalLinks`.

#### `catalog.config_snapshot`

Projection locale de la configuration distante (reçue par `ConfigChangedV1`, appliquée si la version est plus récente).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `key` | `text` | non | clé primaire |
| `updated_at` | `timestamp with time zone` | non |  |
| `value` | `jsonb` | non |  |
| `version` | `integer` | non |  |

#### `catalog.destination`

Destinations actives (Marseille…) : nom FR/EN, centre géographique, ordre d'affichage.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `center` | `geography (point, 4326)` | non |  |
| `created_at` | `timestamp with time zone` | non | défaut `now()` |
| `is_active` | `boolean` | non |  |
| `name_en` | `text` | oui |  |
| `name_fr` | `text` | non |  |
| `slug` | `text` | non |  |
| `sort_order` | `integer` | non |  |

- Index (`slug`) : unique

#### `catalog.external_link`

Liens sortants d'un lieu (Wikipédia, vidéos YouTube, site officiel) ; la miniature est une copie hébergée chez nous.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `channel` | `text` | oui |  |
| `kind` | `text` | non |  |
| `lang` | `text` | non |  |
| `poi_id` | `uuid` | non |  |
| `thumbnail_path` | `text` | oui |  |
| `title` | `text` | non |  |
| `url` | `text` | non |  |
| `video_id` | `text` | oui |  |

- Index (`poi_id`)
- Clé étrangère (`poi_id`) → `catalog.poi`, suppression : Cascade
- Contrainte `ck_external_link_kind` : `kind in ('wikipedia', 'youtube', 'official')`

#### `catalog.poi`

Lieux publiés (projection de Factory) : position `geography`, scores d'importance et de qualité, version, « pépite » (`hidden_gem`).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `content_quality_score` | `real` | non |  |
| `created_at` | `timestamp with time zone` | non | défaut `now()` |
| `destination_id` | `uuid` | non |  |
| `hidden_gem` | `boolean` | non |  |
| `importance_score` | `smallint` | non |  |
| `location` | `geography (point, 4326)` | non |  |
| `published_at` | `timestamp with time zone` | oui |  |
| `slug` | `text` | non |  |
| `taxonomy_version` | `integer` | non |  |
| `updated_at` | `timestamp with time zone` | oui |  |
| `version` | `integer` | non |  |

- Index (`location`) : GIST
- Index (`destination_id`, `slug`) : unique
- Clé étrangère (`destination_id`) → `catalog.destination`, suppression : Restrict
- Contrainte `ck_poi_importance` : `importance_score between 0 and 100`

#### `catalog.poi_ethics`

Indicateurs éthiques d'un lieu : site fragile, accès réglementé.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `poi_id` | `uuid` | non | clé primaire |
| `access_regulated` | `boolean` | non |  |
| `fragile` | `boolean` | non |  |

- Clé étrangère (`poi_id`) → `catalog.poi`, suppression : Cascade

#### `catalog.poi_interest`

Poids d'un lieu sur chaque dimension de la taxonomie (`taxonomy_code`, 0 < poids ≤ 1).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `poi_id` | `uuid` | non | clé primaire (composite) |
| `taxonomy_code` | `text` | non | clé primaire (composite) |
| `weight` | `real` | non |  |

- Clé primaire : (`poi_id`, `taxonomy_code`)
- Index (`taxonomy_code`)
- Clé étrangère (`poi_id`) → `catalog.poi`, suppression : Cascade
- Clé étrangère (`taxonomy_code`) → `catalog.taxonomy_node`, suppression : Restrict
- Contrainte `ck_poi_interest_weight` : `weight > 0 and weight <= 1`

#### `catalog.poi_text`

Nom et description par langue ; `search_vector` (tsvector) et index trigramme pour la recherche.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `poi_id` | `uuid` | non | clé primaire (composite) |
| `lang` | `text` | non | clé primaire (composite) |
| `name` | `text` | non |  |
| `search_vector` | `tsvector` | non |  |
| `short_description` | `text` | oui |  |

- Clé primaire : (`poi_id`, `lang`)
- Index (`name`) : GIN gin_trgm_ops
- Index (`search_vector`) : GIN
- Clé étrangère (`poi_id`) → `catalog.poi`, suppression : Cascade

#### `catalog.story`

Histoires publiées d'un lieu : texte, audio (`audio_path`, `audio_parts` en JSON), durée, mention IA, version, statut.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `audio_parts` | `jsonb` | non |  |
| `audio_path` | `text` | oui |  |
| `duration_seconds` | `integer` | non |  |
| `is_ai_generated` | `boolean` | non |  |
| `is_premium` | `boolean` | non |  |
| `kind` | `text` | non |  |
| `lang` | `text` | non |  |
| `poi_id` | `uuid` | non |  |
| `published_at` | `timestamp with time zone` | oui |  |
| `sources` | `jsonb` | non |  |
| `status` | `text` | non |  |
| `text` | `text` | oui |  |
| `title` | `text` | non |  |
| `version` | `integer` | non |  |

- Index (`poi_id`, `lang`) : partiel `status = 'published'`
- Index (`poi_id`, `lang`, `kind`, `version`) : unique
- Clé étrangère (`poi_id`) → `catalog.poi`, suppression : Cascade
- Contrainte `ck_story_premium_text` : `not is_premium or text is null`
- Contrainte `ck_story_status` : `status in ('published', 'unpublished', 'archived')`

#### `catalog.taxonomy_node`

Taxonomie des centres d'intérêt (annexe D), publiée par Factory.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `code` | `text` | non | clé primaire |
| `level` | `smallint` | non |  |
| `parent_code` | `text` | oui |  |
| `sort_order` | `integer` | non |  |
| `taxonomy_version` | `integer` | non |  |

### Service Discovery — schéma `discovery`

Contexte EF : `OnVoyage.Discovery.Infrastructure/Persistence/DiscoveryDbContext.cs`. Migrations : `20261001134224_Initial`.

#### `discovery.impression`

Cartes de lieux affichées (surface, date) : sert aux signaux d'impression ; conservation courte prévue (`retention.impressions_days`).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `bigint` | non | clé primaire, identité |
| `client_event_id` | `uuid` | non |  |
| `poi_id` | `uuid` | non |  |
| `shown_at` | `timestamp with time zone` | non |  |
| `surface` | `text` | non |  |
| `traveler_id` | `uuid` | non |  |

- Index (`shown_at`)
- Index (`traveler_id`, `client_event_id`) : unique
- Clé étrangère (`traveler_id`) → `discovery.traveler`, suppression : Cascade

#### `discovery.interaction`

Historique des signaux du voyageur (aime, bof, écoute à 80 %…). **Le vecteur d'intérêts est le rejeu de cette table** (ADR-0011). Unique par (`traveler_id`, `client_event_id`).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `bigint` | non | clé primaire, identité |
| `category_code` | `text` | oui |  |
| `client_event_id` | `uuid` | non |  |
| `kind` | `text` | non |  |
| `occurred_at` | `timestamp with time zone` | non |  |
| `poi_id` | `uuid` | oui |  |
| `story_id` | `uuid` | oui |  |
| `story_version` | `integer` | oui |  |
| `traveler_id` | `uuid` | non |  |
| `value` | `double precision` | non |  |

- Index (`traveler_id`, `client_event_id`) : unique
- Index (`traveler_id`, `occurred_at`)
- Clé étrangère (`traveler_id`) → `discovery.traveler`, suppression : Cascade

#### `discovery.interest_vector`

Vecteur d'intérêts courant (`real[]`, une valeur par dimension de la taxonomie), verrous utilisateur en JSON.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `traveler_id` | `uuid` | non | clé primaire |
| `locks` | `jsonb` | non |  |
| `taxonomy_version` | `integer` | non |  |
| `updated_at` | `timestamp with time zone` | non |  |
| `vector` | `real[]` | non |  |

- Clé étrangère (`traveler_id`) → `discovery.traveler`, suppression : Cascade

#### `discovery.onboarding_clip`

Extraits audio de l'onboarding proposés ou actifs, issus des histoires de type extrait publiées par Factory.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `story_id` | `uuid` | non | clé primaire |
| `active` | `boolean` | non |  |
| `audio_path` | `text` | non |  |
| `duration_seconds` | `integer` | non |  |
| `lang` | `text` | non |  |
| `poi_id` | `uuid` | non |  |
| `title` | `text` | non |  |
| `version` | `integer` | non |  |

- Index (`lang`)

#### `discovery.poi_projection`

Projection des lieux publiés lue par le moteur (poids, importance, qualité, fragilité) ; alimentée par `PoiPublishedV1`.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `poi_id` | `uuid` | non | clé primaire |
| `destination` | `text` | non |  |
| `fragile` | `boolean` | non |  |
| `hidden_gem` | `boolean` | non |  |
| `importance` | `double precision` | non |  |
| `is_published` | `boolean` | non |  |
| `quality` | `double precision` | non |  |
| `slug` | `text` | non |  |
| `version` | `integer` | non |  |
| `weights` | `jsonb` | non |  |

#### `discovery.poi_rating`

Dernier avis du voyageur sur un lieu (note, lieu écarté).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `traveler_id` | `uuid` | non | clé primaire (composite) |
| `poi_id` | `uuid` | non | clé primaire (composite) |
| `excluded` | `boolean` | non |  |
| `rating` | `double precision` | non |  |
| `updated_at` | `timestamp with time zone` | non |  |

- Clé primaire : (`traveler_id`, `poi_id`)
- Clé étrangère (`traveler_id`) → `discovery.traveler`, suppression : Cascade

#### `discovery.traveler`

Profil de service d'un voyageur : cohorte (`control` ou `personalized`), mode éthique, langue, profondeur du profil. Même identifiant que `platform.account.id`.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `cohort` | `text` | non |  |
| `created_at` | `timestamp with time zone` | non | défaut `now()` |
| `ethical_mode` | `text` | non |  |
| `is_premium` | `boolean` | non |  |
| `lang` | `text` | non |  |
| `last_active_at` | `timestamp with time zone` | non |  |
| `profile_depth` | `integer` | non |  |

- Contrainte `ck_traveler_cohort` : `cohort in ('control', 'personalized')`

#### `discovery.visit`

Visites probables : lieu, jour, durée, confiance. **Aucune colonne de position** (vérifié par un test de schéma).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `bigint` | non | clé primaire, identité |
| `client_event_id` | `uuid` | non |  |
| `confidence` | `double precision` | non |  |
| `dwell_s` | `integer` | non |  |
| `poi_id` | `uuid` | non |  |
| `traveler_id` | `uuid` | non |  |
| `visited_on` | `date` | non |  |

- Index (`traveler_id`, `client_event_id`) : unique
- Clé étrangère (`traveler_id`) → `discovery.traveler`, suppression : Cascade

### Service Factory — schéma `factory`, `factory_raw`

Contexte EF : `OnVoyage.Factory.Infrastructure/Persistence/FactoryDbContext.cs`. Migrations : `20261001090828_InitialFactory`, `20261001093509_ContentPipeline`, `20261001102331_AuditLog`, `20261001105136_GenerationBatches`, `20261001112858_ReportInbox`, `20261001120942_PlaceVideos`.

#### `factory.audit_log`

Journal d'audit des actions du back-office Factory (qui, quoi, cible, statut).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `action` | `text` | non |  |
| `actor` | `text` | non |  |
| `at` | `timestamp with time zone` | non |  |
| `detail` | `text` | oui |  |
| `status` | `integer` | non |  |
| `target` | `text` | non |  |

- Index (`at`)

#### `factory.dedup_link`

Propositions et décisions de fusion de doublons, avec distance, similarité, état et annulation.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `automatic` | `boolean` | non |  |
| `created_at` | `timestamp with time zone` | non |  |
| `distance_meters` | `double precision` | non |  |
| `inherited_qid` | `text` | oui |  |
| `kept_place_id` | `uuid` | non |  |
| `other_place_id` | `uuid` | non |  |
| `reason` | `text` | non |  |
| `reverted_at` | `timestamp with time zone` | oui |  |
| `similarity` | `double precision` | non |  |
| `state` | `text` | non |  |

- Index (`kept_place_id`)
- Index (`other_place_id`)

#### `factory.fact`

Faits extraits des documents sources (énoncé, citation, confiance, statut accepté ou rejeté) : seule matière du rédacteur (monde fermé).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `confidence` | `double precision` | non |  |
| `created_at` | `timestamp with time zone` | non |  |
| `document_id` | `uuid` | non |  |
| `place_id` | `uuid` | non |  |
| `quote` | `text` | non |  |
| `reason` | `text` | oui |  |
| `statement` | `text` | non |  |
| `status` | `text` | non |  |
| `type` | `text` | non |  |

- Index (`document_id`)
- Index (`place_id`)
- Contrainte `ck_fact_confidence` : `confidence between 0 and 1`

#### `factory.generation_batch`

Lot de génération demandé par l'éditeur (critères en JSON, total).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `created_at` | `timestamp with time zone` | non |  |
| `created_by` | `text` | non |  |
| `criteria` | `jsonb` | non |  |
| `total` | `integer` | non |  |

- Index (`created_at`)

#### `factory.generation_job`

Tâche d'un lot : étape, état, tentatives, dernière erreur, lieu, histoire produite.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `attempts` | `integer` | non |  |
| `batch_id` | `uuid` | non |  |
| `kind` | `text` | non |  |
| `lang` | `text` | non |  |
| `last_error` | `text` | oui |  |
| `outcome` | `text` | oui |  |
| `place_id` | `uuid` | non |  |
| `place_name` | `text` | non |  |
| `state` | `text` | non |  |
| `step` | `text` | non |  |
| `story_id` | `uuid` | oui |  |
| `updated_at` | `timestamp with time zone` | non |  |

- Index (`batch_id`, `state`)
- Contrainte `ck_generation_job_state` : `state in ('Pending', 'Running', 'Succeeded', 'Failed')`

#### `factory.import_run`

Exécutions d'import OSM : destination, table brute `factory_raw.osm_place_*`, nombre de lignes créées ou mises à jour.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `created` | `integer` | non |  |
| `destination_slug` | `text` | non |  |
| `raw_rows` | `integer` | non |  |
| `raw_table` | `text` | non |  |
| `source` | `text` | non |  |
| `started_at` | `timestamp with time zone` | non |  |
| `updated` | `integer` | non |  |

#### `factory.llm_call`

Appels aux modèles : modèle, version du prompt, jetons, coût estimé en USD, durée, succès. Aucune donnée de voyageur.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `content_id` | `uuid` | oui |  |
| `cost_usd` | `double precision` | non |  |
| `created_at` | `timestamp with time zone` | non |  |
| `duration_ms` | `integer` | non |  |
| `input_tokens` | `integer` | non |  |
| `kind` | `text` | non |  |
| `model` | `text` | non |  |
| `output_tokens` | `integer` | non |  |
| `prompt_id` | `text` | oui |  |
| `prompt_version` | `text` | oui |  |
| `succeeded` | `boolean` | non |  |

- Index (`created_at`)

#### `factory.place`

Lieux candidats puis publiés : géométrie, tags OSM, Wikidata, scores, classification, statut, version publiée, affluence, éthique.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `access_regulated` | `boolean` | non |  |
| `annual_pageviews` | `bigint` | non |  |
| `classification_confidence` | `real` | non |  |
| `classification_outcome` | `text` | oui |  |
| `created_at` | `timestamp with time zone` | non |  |
| `crowd_offpeak` | `smallint` | non |  |
| `crowd_peak` | `smallint` | non |  |
| `crowd_shoulder` | `smallint` | non |  |
| `destination_slug` | `text` | non |  |
| `editorially_saturated` | `boolean` | non |  |
| `footprint` | `geometry (multipolygon, 4326)` | oui |  |
| `fragile` | `boolean` | non |  |
| `hidden_gem` | `boolean` | non |  |
| `import_run_id` | `uuid` | non |  |
| `importance_override` | `smallint` | oui |  |
| `importance_score` | `smallint` | oui |  |
| `location` | `geography (point, 4326)` | non |  |
| `merged_into` | `uuid` | oui |  |
| `name` | `text` | non |  |
| `name_en` | `text` | oui |  |
| `osm_id` | `bigint` | non |  |
| `osm_tags` | `jsonb` | non |  |
| `osm_type` | `text` | non |  |
| `osm_version` | `integer` | oui |  |
| `popularity_percentile` | `smallint` | oui |  |
| `published_version` | `integer` | non |  |
| `qid` | `text` | oui |  |
| `retrieved_at` | `timestamp with time zone` | non |  |
| `slug` | `text` | non |  |
| `source` | `text` | non |  |
| `source_license` | `text` | non |  |
| `source_url` | `text` | non |  |
| `status` | `text` | non |  |
| `updated_at` | `timestamp with time zone` | non |  |

- Index (`location`) : GIST
- Index (`name`) : GIN gin_trgm_ops
- Index (`qid`)
- Index (`destination_slug`, `slug`) : unique
- Index (`destination_slug`, `status`)
- Index (`osm_type`, `osm_id`) : unique
- Contrainte `ck_place_crowd` : `crowd_offpeak between 1 and 5 and crowd_shoulder between 1 and 5 and crowd_peak between 1 and 5`
- Contrainte `ck_place_importance` : `importance_score is null or importance_score between 0 and 100`

#### `factory.place_interest`

Poids d'un lieu par dimension de la taxonomie, avec sa source (règle, modèle, éditeur).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `place_id` | `uuid` | non | clé primaire (composite) |
| `taxonomy_code` | `text` | non | clé primaire (composite) |
| `source` | `text` | non |  |
| `weight` | `real` | non |  |

- Clé primaire : (`place_id`, `taxonomy_code`)
- Clé étrangère (`place_id`) → `factory.place`, suppression : Cascade
- Contrainte `ck_place_interest_weight` : `weight > 0 and weight <= 1`

#### `factory.place_video`

Vidéos YouTube choisies par un éditeur pour un lieu (liens sortants).

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `place_id` | `uuid` | non | clé primaire (composite) |
| `video_id` | `text` | non | clé primaire (composite) |
| `channel` | `text` | non |  |
| `selected_at` | `timestamp with time zone` | non |  |
| `thumbnail_path` | `text` | non |  |
| `title` | `text` | non |  |
| `url` | `text` | non |  |

- Clé primaire : (`place_id`, `video_id`)

#### `factory.pronunciation`

Dictionnaire de prononciation par destination, appliqué avant la synthèse vocale.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `destination_slug` | `text` | non | clé primaire (composite) |
| `term` | `text` | non | clé primaire (composite) |
| `replacement` | `text` | non |  |

- Clé primaire : (`destination_slug`, `term`)

#### `factory.source_document`

Documents sources (texte Wikipédia…, licence, éditeur, empreinte SHA-256) à partir desquels les faits sont extraits.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `language` | `text` | non |  |
| `license` | `text` | non |  |
| `place_id` | `uuid` | non |  |
| `publisher` | `text` | oui |  |
| `quality` | `double precision` | non |  |
| `retrieved_at` | `timestamp with time zone` | non |  |
| `revision` | `text` | oui |  |
| `sha256` | `text` | non |  |
| `text` | `text` | non |  |
| `title` | `text` | non |  |
| `type` | `text` | non |  |
| `url` | `text` | non |  |

- Index (`place_id`, `url`)

#### `factory.story`

Histoires générées ou éditées : texte, statut du cycle de vie (§8.2), rapport de contrôles, version du prompt, modèle, voix, scores.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `announce_front` | `text` | non |  |
| `announce_left` | `text` | non |  |
| `announce_right` | `text` | non |  |
| `care_note` | `text` | oui |  |
| `check_report` | `jsonb` | non |  |
| `created_at` | `timestamp with time zone` | non |  |
| `editorial_score` | `double precision` | non |  |
| `estimated_duration_seconds` | `integer` | non |  |
| `facts_used` | `uuid[]` | non |  |
| `hook` | `text` | non |  |
| `kind` | `text` | non |  |
| `lang` | `text` | non |  |
| `model` | `text` | non |  |
| `place_id` | `uuid` | non |  |
| `prompt_version` | `text` | non |  |
| `published_at` | `timestamp with time zone` | oui |  |
| `quality_score` | `double precision` | non |  |
| `rejected_reason` | `text` | oui |  |
| `remote_intro` | `text` | non |  |
| `status` | `text` | non |  |
| `text` | `text` | non |  |
| `title` | `text` | non |  |
| `updated_at` | `timestamp with time zone` | non |  |
| `version` | `integer` | non |  |
| `voice_id` | `text` | non |  |

- Index (`place_id`, `lang`, `kind`, `version`) : unique

#### `factory.story_audio_part`

Fichiers audio d'une histoire (introduction, corps, annonces) : chemin, durée, empreinte.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `story_id` | `uuid` | non | clé primaire (composite) |
| `part` | `text` | non | clé primaire (composite) |
| `bytes` | `bigint` | non |  |
| `duration_seconds` | `integer` | non |  |
| `path` | `text` | non |  |
| `sha256` | `text` | non |  |

- Clé primaire : (`story_id`, `part`)

#### `factory.story_report`

Signalements d'erreur par les voyageurs : `traveler_id` et motif (500 caractères au plus) ; traités par l'éditeur.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `id` | `uuid` | non | clé primaire |
| `created_at` | `timestamp with time zone` | non |  |
| `handled_at` | `timestamp with time zone` | oui |  |
| `reason` | `text` | non |  |
| `resolution` | `text` | oui |  |
| `status` | `text` | non |  |
| `story_id` | `uuid` | non |  |
| `traveler_id` | `uuid` | non |  |

- Index (`status`, `created_at`)
- Index (`story_id`, `traveler_id`) : unique
- Index (`traveler_id`, `created_at`)
- Contrainte `ck_story_report_status` : `status in ('Open', 'Handled', 'Dismissed')`

#### `factory_raw.wikidata_entity`

Cache des entités Wikidata (libellés, descriptions, statuts patrimoniaux, sitelinks) avec licence et source.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `qid` | `text` | non | clé primaire |
| `description_en` | `text` | oui |  |
| `description_fr` | `text` | oui |  |
| `heritage_statuses` | `text[]` | non |  |
| `image` | `text` | oui |  |
| `inception` | `text` | oui |  |
| `instance_of` | `text[]` | non |  |
| `label_en` | `text` | oui |  |
| `label_fr` | `text` | oui |  |
| `retrieved_at` | `timestamp with time zone` | non |  |
| `sitelinks` | `integer` | non |  |
| `source` | `text` | non |  |
| `source_license` | `text` | non |  |
| `source_url` | `text` | non |  |
| `website` | `text` | oui |  |
| `wikipedia_en` | `text` | oui |  |
| `wikipedia_fr` | `text` | oui |  |

#### `factory_raw.wikipedia_pageviews`

Pages vues sur 12 mois par entité et langue (popularité), avec licence.

| Colonne | Type | Nul | Remarque |
| --- | --- | --- | --- |
| `qid` | `text` | non | clé primaire (composite) |
| `language` | `text` | non | clé primaire (composite) |
| `retrieved_at` | `timestamp with time zone` | non |  |
| `source_license` | `text` | non |  |
| `title` | `text` | non |  |
| `views12_months` | `bigint` | non |  |

- Clé primaire : (`qid`, `language`)

### Service Creators — schéma `creators`

Une migration (`Initial`). Colonnes en `snake_case`. Pas de colonne de position (test d'intégration sur le schéma). Détail des décisions : [ADR-0016](adr/0016-creators.md).

| Table | Clé | Colonnes principales | Index et contraintes |
| --- | --- | --- | --- |
| `creators.creator` | `id` | `account_id`, `handle citext` (30), `display_name`, `bio`, `avatar_path`, `languages text[]`, `specialties text[]`, `destination_ids uuid[]`, `links jsonb`, `status`, `terms_version`, `terms_document_ref`, `terms_accepted_at`, `founding`, `created_at`, `updated_at` | `ux_creator_handle` (unique, insensible à la casse), `ux_creator_account` (unique, `account_id` non nul), `status in (draft, published, suspended)` |
| `creators.content_item` | `id` | `creator_id` (cascade), `platform`, `external_id`, `permalink`, `title`, `caption_excerpt` (≤ 500), `published_at`, `duration_s`, `kind`, `cover_path`, `chapters jsonb`, `is_commercial`, `status`, `created_at` | `ux_content_item_platform_external_id` (unique) ; plateforme, type et statut contraints |
| `creators.place_link` | `id` | `content_id` (cascade, nul pour un conseil seul), `creator_id` (cascade), `poi_id`, `start_s`, `confidence real`, `signals jsonb`, `status`, `validated_at`, `created_at` | unique (`creator_id`, `poi_id`, `content_id`, `start_s`) avec valeurs nulles égales ; (`poi_id`, `status`) ; `status in (proposed, validated, rejected)` |
| `creators.creator_tip` | (`creator_id`, `poi_id`) | `id` (unique, cible possible d'un signalement), `text` (≤ 280), `updated_at`, `status` | `status in (published, hidden)` |
| `creators.follow` | (`traveler_id`, `creator_id`) | `followed_at` | index sur `creator_id` ; **jamais exposé** |
| `creators.poi_directory` | `poi_id` | `destination_id`, `destination_slug`, `name`, `names jsonb` (fr, en, alias), `city`, `importance_score`, `is_published`, `version`, `search_text`, `updated_at` | GIN trigramme sur `search_text` ; **pas de coordonnées** |
| `creators.moderation_case` | `id` | `target_type`, `target_id`, `reason`, `reporter_ref` (nulle après suppression du compte), `status`, `decision`, `statement_of_reasons`, `created_at`, `decided_at` | (`status`, `created_at`), `reporter_ref`, (`target_type`, `target_id`) |

Hors de cette première livraison : `connected_account`, `creator_list`, `creator_list_item`, `creator_stats_daily` du §11.5.
