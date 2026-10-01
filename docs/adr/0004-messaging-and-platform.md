# ADR-0004 — Messagerie Wolverine (T-006) et service Platform (T-007)

## Messagerie
- `OnVoyage.Messaging` (projet partagé) configure chaque service : boîtes de réception et d'envoi durables dans le schéma du service, transport en files PostgreSQL (schéma `wolverine_queues`, une file par service consommateur), transactions automatiques, relances progressives sur `TimeoutException` / `NpgsqlException`, puis file d'erreurs.
- **Une seule base `onvoyage`, un schéma par service** (§9.6) : le transport en files l'impose. Aspire crée la base ; chaque service applique ses migrations dans son schéma.
- Publication transactionnelle : l'adaptateur d'infrastructure (`IDbContextOutbox`) écrit les lignes métier et l'événement dans la même transaction ; les handlers (Application) ne connaissent ni EF ni Wolverine côté données.
- Consommation idempotente : un consommateur n'applique un événement que si sa version est plus récente (`config_snapshot`), donc redélivrance et désordre sont sans effet.

## Platform
- Schéma `platform` : `remote_config` (+ `remote_config_history`, append-only), `feature_flag`, `consent`. L'annexe E est semée telle quelle (`Seed/default-config.json`, 21 clés), avec les 11 drapeaux du §18 (seul `control_cohort` est actif au MVP-0).
- API : `GET /config` (anonyme ; sans `security` ni `deletion`), `scope=edge` (interne, uniquement `security`), `PUT /admin/config/{key}`, `/admin/flags/{name}`, `GET/PUT /me/consents`. Les erreurs sont des Problem Details avec un `type` stable.
- Drapeaux : activation, plateformes, version minimale, pourcentage de déploiement par seau stable `hash(drapeau:voyageur) mod 100` ; sans identité, un déploiement partiel reste éteint. Évaluateur maison (pas de `Microsoft.FeatureManagement`, qui n'ajoute rien ici).
- Au démarrage, Platform republie la version courante de chaque clé (`ConfigChangedV1`) : un service qui démarre après Platform, ou qui a perdu des messages, se remet à niveau.
- Catalog : projection `catalog.config_snapshot`, `ConfigChangedHandler` et garde de version `426 Upgrade Required` (`X-App-Version` < `app.min_app_version`) fournie par `ServiceDefaults`.

## Écarts assumés
- **Accès provisoire** (en attendant Supabase Auth, T-004) : l'administration exige `X-Admin-Key` (= `Auth:AdminApiKey`, désactivée si vide, comparaison à temps constant) ; les consentements lisent `X-Traveler-Id` seulement si `Auth:AllowTravelerIdHeader=true` (jamais en production). À remplacer par le JWT et les rôles.
- Pas encore : `TravelerDeletionRequestedV1` / export (T-507), journal d'audit (T-409), `EntitlementChangedV1`, l'abonnement des autres services aux files, la lecture de `scope=edge` par le Gateway.
- `IMemoryCache` (30 s) pour la garde de version, à la place de `HybridCache`.
