# API — ON.VOYAGE

Source : les fichiers `Api/Endpoints/*.cs` de chaque service, `src/Gateway/OnVoyage.Gateway/appsettings.json` et `src/Aspire/OnVoyage.ServiceDefaults/Security/Authentication.cs`, relus au 2026-10-01. Ce document décrit **ce qui existe**, pas le §12 du cahier des charges : les écarts sont listés en fin de document. Il n'y a pas encore de document OpenAPI généré (`docs/api/*.json` du §22 reste à produire, aucun service n'expose Swagger).

## Conventions

| Sujet | Règle |
| --- | --- |
| Accès | Les clients (app, PWA, back-office) n'appellent que le **Gateway** : `/api/{service}/v1/…`. Les services ne sont pas joignables depuis l'extérieur. |
| Format | JSON (`System.Text.Json`, noms en camelCase). Les énumérations de Factory sont écrites en chaînes. Les identifiants sont des UUID v7. |
| Authentification | `Authorization: Bearer <jeton d'accès>`. Jeton JWT HS256 émis par Platform (`iss` = `aud` = `on.voyage`), validé par le Gateway **et** par chaque service ; durée 60 min (`auth.access_token_minutes`). Session anonyme ou compte : voir Platform. |
| Politiques | `traveler` (tout jeton valide, anonyme compris), `account` (`email_verified` = vrai), `admin` (rôle `admin`), `creator`, `internal` (jeton de service, rôle `internal`), `traveler_or_internal`. `account` et `creator` sont définies mais aucun endpoint ne les exige encore. |
| Erreurs | Problem Details (RFC 9457, `application/problem+json`) avec un `type` stable `https://on.voyage/problems/{code}`. Les codes d'erreur métier sont des identifiants courts (`validation`, `unknown_destination`, `otp_cooldown`…). Les services appellent `AddProblemDetails()` ; le Gateway répond aussi en Problem Details. |
| Correspondance code → statut | Catalog et Discovery : `*not_found` → 404, tout autre → 400. Platform : `otp_cooldown` et `otp_rate_limited` → 429 (avec `Retry-After`), `invalid_refresh_token` → 401, `email_already_linked` → 409, `email_unavailable` → 502, `*not_found` → 404, autre → 400. Factory : `*not_found` → 404, `validation` → 400, `youtube_not_configured` → 503, `youtube_unavailable` → 502, autre → 409. |
| Version minimale | Si l'en-tête `X-App-Version` (ex. `1.0.0`) est inférieur à `app.min_app_version` de la configuration distante, les routes `/api` du **Catalog** répondent `426` (`upgrade_required`, avec `minAppVersion`). Seul Catalog applique ce contrôle aujourd'hui. |
| Position | Les coordonnées ne voyagent que dans les paramètres `lat` et `lon` de `GET …/destinations/{slug}/pois`. Elles ne sont ni stockées ni journalisées (voir `docs/PRIVACY.md`). |
| Idempotence | Les envois d'interactions portent un `clientEventId` : le renvoyer ne change rien. |

### Protection du Gateway (SEC-04, F-24)

| Mécanisme | Comportement |
| --- | --- |
| Robots | Un `User-Agent` contenant l'un des jetons de `security.blocked_user_agents` (GPTBot, ClaudeBot, Google-Extended…) reçoit `403` (`blocked_client`), sur tous les chemins sauf `/health` et `/alive`. |
| Débit | Fenêtre glissante d'une minute : 300 requêtes par adresse (`security.rate_per_ip_per_min`) puis 120 par voyageur authentifié (`security.rate_per_traveler_per_min`). Dépassement : `429` (`rate_limited`) avec `Retry-After`. `/health` et `/alive` sont exemptés. Les valeurs sont relues de Platform (`GET /api/platform/v1/config?scope=edge`, jeton interne) toutes les 60 s. |
| Adresse du client | Derrière Caddy, `Gateway:TrustForwardedHeaders=true` : l'adresse vient de `X-Forwarded-For` (une seule valeur de confiance). |
| CORS | Origines de `Cors:AllowedOrigins` seulement ; méthodes `GET`, `POST`, `OPTIONS`. |

## Routes du Gateway

Configuration YARP de `appsettings.json` (section `ReverseProxy`). « Politique » est celle du **Gateway** ; les services appliquent leur propre politique (défense en profondeur).

| Route | Chemin | Méthodes | Politique du Gateway | Destination |
| --- | --- | --- | --- | --- |
| `catalog` | `/api/catalog/{**}` | toutes | `traveler_or_internal` | catalog-api |
| `media` | `/media/{**}` | GET, HEAD | anonyme | catalog-api (fichiers audio de Factory) |
| `discovery-admin` | `/api/discovery/v1/admin/{**}` | toutes | `admin` | discovery-api |
| `discovery` | `/api/discovery/{**}` | toutes | `traveler` | discovery-api |
| `platform-public` | `/api/platform/v1/{auth|config}/{**}` | toutes | anonyme | platform-api |
| `platform` | `/api/platform/{**}` | toutes | `traveler` | platform-api |
| `insights` | `/api/insights/v1/kpis` | GET | `admin` | insights-api (**pas encore déployé** : la route répond une erreur du Gateway) |
| `creators-admin` | `/api/creators/v1/admin/{**}` | toutes | `admin` | creators-api |
| `creators` | `/api/creators/{**}` | toutes | `traveler` | creators-api |
| `factory-story-reports` | `/api/factory/v1/stories/{id}/reports` | POST | `traveler` | factory-api |
| `factory` | `/api/factory/{**}` | toutes | `admin` | factory-api |

Santé : chaque hôte expose `GET /health` (toutes les vérifications) et `GET /alive` (vivacité), sans authentification. Platform y ajoute `platform-db` (tag `ready`).

## Platform — `/api/platform/v1`

| Méthode et chemin | Politique | Paramètres / corps | Réponse | Remarques |
| --- | --- | --- | --- | --- |
| `GET /config` | anonyme | requête : `platform`, `appVersion`, `scope` (`edge` réservé au rôle `internal`, sinon 403) | `ClientConfigDto` : `revision`, `config` (clé → JSON), `flags` (nom → booléen) | Un jeton éventuel ne sert qu'à choisir la tranche de déploiement. |
| `POST /auth/anonymous` | anonyme | — | `AuthSessionDto` | Crée un compte anonyme et sa session. |
| `POST /auth/refresh` | anonyme | `{ refreshToken }` | `AuthSessionDto` | 401 `invalid_refresh_token`. |
| `POST /auth/signout` | anonyme | `{ refreshToken }` | 204 | Révoque le jeton de rafraîchissement. |
| `POST /auth/otp/request` | anonyme | `{ email }` | 202 | Envoie un code à 6 chiffres par e-mail (Resend). 429 : délai de renvoi ou plafond horaire. |
| `POST /auth/otp/verify` | anonyme (jeton facultatif) | `{ email, code }` | `AuthSessionDto` | Avec le jeton d'un compte anonyme, l'e-mail y est **rattaché** (même `travelerId`) ; 409 `email_already_linked`. |
| `GET /me` | `traveler` | — | `AccountDto` | |
| `GET /me/consents` | `traveler` | — | liste de `ConsentDto` | `analytics`, `ads_personalization` ; absence = refusé. |
| `PUT /me/consents/{kind}` | `traveler` | `{ granted, textVersion }` | `ConsentDto` | Publie `ConsentChangedV1`. |
| `GET /admin/config` | `admin` | — | liste de `ConfigEntryDto` | |
| `GET /admin/config/{key}` | `admin` | — | `ConfigEntryDto` | |
| `GET /admin/config/{key}/history` | `admin` | — | versions successives | |
| `PUT /admin/config/{key}` | `admin` | `{ value }` (JSON libre) | `ConfigEntryDto` | Versionne, journalise, publie `ConfigChangedV1`. |
| `GET /admin/flags` | `admin` | — | liste de `FeatureFlagDto` | |
| `PUT /admin/flags/{name}` | `admin` | `{ enabled, rolloutPercent, platforms?, minAppVersion? }` | `FeatureFlagDto` | |
| `GET /admin/audit` | `admin` | requête : `limit` (100), `service`, `actor` | liste de `AdminActionDto` | Journal des actions d'administration de tous les services (SEC-10). |

`AuthSessionDto` : `accessToken`, `accessTokenExpiresAt`, `refreshToken`, `refreshTokenExpiresAt`, `travelerId`, `isAnonymous`, `email`, `roles`.

## Catalog — `/api/catalog/v1`

Groupe entier en `traveler_or_internal` (un voyageur, anonyme compris, ou un hôte interne comme Web.Public). Lecture seule.

| Méthode et chemin | Paramètres | Réponse | Remarques |
| --- | --- | --- | --- |
| `GET /destinations/{slug}` | — | `DestinationDto` : `slug`, `name`, `latitude`, `longitude`, `poiCount` | 404 si inconnue. |
| `GET /destinations/{slug}/pois` | requête : `lat`, `lon` (ensemble ou absents), `radius` en mètres (défaut 50 000 ; 50 à 200 000), `limit` (défaut 100 ; 1 à 500) | liste de `PoiSummaryDto` : id, slug, nom, catégorie, coordonnées, importance, qualité, `crowdLevel`, `hiddenGem`, `distanceMeters`, `audioSeconds`, `weights`, `storyId`, `fragile`, `audioParts` | Sans `lat`/`lon`, pas de distance. Seule route qui reçoit une position. |
| `GET /pois/{slug}` | — | `PoiDetailDto` : histoires (`StoryDto` avec `text`, `audioUrl`, `aiGenerated`, `audioParts`), `attributions`, `links` (`LinkDto` : Wikipédia, vidéos, site officiel) | |

Fichiers audio : `GET /media/{chemin}` sur le Gateway (anonyme, GET et HEAD, requêtes `Range` comprises : réponse 206), servis par Catalog depuis `Media:RootPath`. Les adresses renvoyées dans `audioUrl` et `audioParts` commencent par `Media:PublicBaseUrl`, qui doit être l'adresse **publique du Gateway** (`http://localhost:5080/media` en local). Une histoire sans audio (aucune voix configurée) a `audioUrl = null` et son `text`.

## Discovery — `/api/discovery/v1`

| Méthode et chemin | Politique | Corps / paramètres | Réponse | Remarques |
| --- | --- | --- | --- | --- |
| `GET /onboarding/clips` | `traveler` | requête : `lang` (défaut `fr`) | liste de `OnboardingClipDto` | Extraits d'onboarding actifs dans la langue demandée. |
| `POST /onboarding` | `traveler` | `OnboardingRequest` : `clips` (`storyId`, `liked`), `likedCategories`, `dislikedCategories` | `InteractionBatchResponse` | Idempotent : le même écran renvoyé donne le même profil. |
| `GET /me/profile` | `traveler` | — | `ProfileDto` : `vector`, `profileDepth`, `level`, `taxonomyVersion`, `locks`, `excluded`, `cohort` | |
| `PATCH /me/profile` | `traveler` | `{ corrections: [{ code, value }] }` | `ProfileDto` | `value` fixe la dimension et la verrouille 30 jours ; `null` retire le verrou. |
| `POST /me/interactions` | `traveler` | `{ interactions: [InteractionDto] }`, 1 à 200 éléments | `InteractionBatchResponse` : `vector`, `profileDepth`, `taxonomyVersion`, `accepted`, `duplicates`, `excluded` | Types : `onboarding_up`, `onboarding_down`, `onboarding_category`, `like`, `meh`, `dislike_poi`, `dislike_category`, `listen_80`, `replay`, `abandon_early`, `save`, `navigate`, `visit`, `external_link`, `creator_content_opened`, `impression`. Une visite porte `confidence` et `dwellS`, jamais de coordonnées. `occurredAt` ne peut pas dépasser l'heure serveur de plus de 10 min. |
| `GET /me/candidates?destination` | `traveler` | — | `CandidatesDto` : par lieu les drapeaux, `baseScore` (avec `CreatorSignal`) et les histoires (`audioParts`, `textOnly`) | `textOnly` : histoire sans partie `main` (publiée sans voix de synthèse) ; l'app lit son texte, pris dans le Catalog, avec la voix de l'appareil. Réglage `Discovery:AllowTextOnlyStories`. |
| `GET /me/cf-scores?destination` | `traveler` | — | `CfScoresDto` : par lieu `cf` (nul tant que le filtrage collaboratif n'existe pas) et `creatorSignal` ∈ [0, 1] | Pour le hors ligne (§6.12, §6.15). 404 si la destination est inconnue. Cohorte témoin : signaux à 0. |
| `GET /creators/for-me?destination&limit` | `traveler` | `destination` (défaut `marseille`), `limit` 1–50 (défaut 20) | `CreatorsForMeDto` : `items` (`creatorId`, `handle`, `displayName`, `avatarPath`, `specialties`, `placeCount`, `affinity` en %, `following`) | Créateurs publiés ayant validé un lieu publié de la destination, triés par affinité `A(u, c)` (§6.15), puis nombre de lieux, puis handle. Le vecteur `c` n'est jamais renvoyé. Cohorte témoin : ordre sans profil, affinité 0. |
| `GET /admin/onboarding-clips` | `admin` | requête : `lang` | `AdminClipsDto` (`active`, `candidates`) | |
| `PUT /admin/onboarding-clips` | `admin` | `{ storyIds }` : exactement 5 extraits de 5 catégories de niveau 1 différentes | `true` | 400 sinon. |

## Creators — `/api/creators/v1`

Voyageur (politique `traveler`, sessions anonymes comprises ; aucune route n'accepte une position) :

| Méthode et chemin | Réponse | Remarques |
| --- | --- | --- |
| `GET /creators/{handle}` | `CreatorPageDto` | Insensible à la casse. 404 si le créateur n'est pas publié. Abonnés : `null` sous 20 (`isNew`). Lieux, contenus et liens : validés, en ligne, lieu publié seulement. |
| `GET /creators?destination&specialty&cursor&limit` | `CreatorListDto` | Tri par nombre de lieux validés ; `cursor` est un décalage ; 20 par page, 50 au plus. |
| `GET /pois/{poiId}/contents?limit` | `PoiCreatorsDto` | Bloc « Vu par les créateurs » : un élément par créateur, conseil, contenu (lien horodaté si chapitre, drapeau `isCommercial`). Sans vecteur `c` : l'affinité vient de `GET /api/discovery/v1/creators/for-me` (T-1205). |
| `PUT` / `DELETE /me/follows/{creatorId}` | `FollowStateDto` | Idempotent ; `FollowChangedV1` publié au premier changement seulement. 404 si le créateur n'est pas publié. |
| `GET /me/follows` | liste de `FollowedCreatorDto` | Les créateurs publiés que le voyageur suit. |
| `POST /reports` | 202, `ReportReceiptDto` | `targetType` : `creator`, `content`, `place_link`, `tip` ; `reason` : `inaccurate`, `misleading`, `undeclared_ad`, `inappropriate`, `impersonation`, `other`. Un doublon ouvert renvoie le même dossier. |

Administrateur (politique `admin`, préfixe `/admin`, chaque écriture est journalisée dans Platform) :

| Méthode et chemin | Remarques |
| --- | --- |
| `GET /creators?status&search&limit`, `GET /creators/{id}` | Liste et fiche ; `publishBlock` dit pourquoi la publication est impossible (`terms_required`, `specialty_required`). |
| `POST /creators` | Crée un créateur fondateur en brouillon. 409 `handle_taken` avec une suggestion (`suggestion`). |
| `PUT /creators/{id}` | Profil. Un profil publié ne peut pas perdre sa dernière spécialité. |
| `PUT /creators/{id}/consent` | Consentement fondateur : `documentRef` obligatoire (`terms_version = fondateur`). |
| `PUT /creators/{id}/account` | Rattache le compte (un compte, un créateur). Avec le consentement, `CreatorTermsAcceptedV1` fait donner le rôle `creator` par Platform. |
| `POST /creators/{id}/publish`, `/unpublish`, `/suspend` | 422 `terms_required` ou `specialty_required` ; motif obligatoire pour dépublier et suspendre. |
| `POST /creators/{id}/handle-claim` | Le créateur réclame un handle ; son détenteur est dépublié et renommé. |
| `POST` / `PUT` / `DELETE /creators/{id}/contents[/{contentId}]` | Contenus par URL (YouTube, Instagram, TikTok), chapitres, mention « Publicité ». |
| `POST` / `PUT` / `DELETE /creators/{id}/place-links[/{linkId}]` | Associations ; validée d'emblée. |
| `PUT` / `DELETE /creators/{id}/tips/{poiId}` | Conseil (280 caractères) ; crée l'association « conseil seul » si le créateur n'en a pas. |
| `GET /places?query&destination&limit` | Recherche dans le répertoire (sans accents ni casse). |
| `GET /moderation?status&limit`, `POST /moderation/{caseId}/decision` | File des signalements (jamais l'auteur) ; décision `dismissed` ou `upheld` (exposé des motifs obligatoire). |

Erreurs : Problem Details avec `type` = `https://on.voyage/problems/<code>` et l'extension `code`.

## Factory — `/api/factory/v1` (back-office)

Tout est en politique `admin`, sauf le signalement. Chaque écriture est consignée au journal d'audit de Factory (qui, quoi, cible, statut) et publiée à Platform (`AdminActionRecordedV1`). Les tâches longues sont postées sur la file `factory` du worker : la réponse est `202 Accepted`.

| Méthode et chemin | Corps / paramètres | Réponse |
| --- | --- | --- |
| `POST /stories/{id}/reports` | `{ reason }` — **politique `traveler`** (F-20) | `{ received: true }` ; `reason` porte le type en préfixe, `[Pronunciation] Le nom…` (sans préfixe connu : fait inexact) ; seuls trois lecteurs distincts signalant un **fait inexact** suspendent l'histoire |
| `POST /admin/imports`, `/admin/enrichments`, `/admin/scorings` | `{ destination }` | 202 |
| `POST /admin/snapshot-imports` | `{ destination }` | 202 : charge le snapshot versionné `data-pipeline/<destination>/` (idempotent, [ADR-0017](adr/0017-snapshot-et-amorcage-d-une-destination.md)) |
| `POST /admin/bootstrap` | `{ destination, maxPlaces?, minImportance?, lang?, budgetUsd, autoPublish?, forceImport?, skipImport?, allowUnpriced? }` | 202 `{ id }` : enregistre l'exécution (`Queued`) puis la confie au worker ; amorçage plafonné par `budgetUsd` ([runbook](runbooks/bootstrap-marseille.md)). 400 `validation`, 404 `destination_not_found`, 409 `prices_missing` |
| `GET /admin/bootstrap-runs?limit=`, `GET /admin/bootstrap-runs/{id}` | — | exécutions d'amorçage (état `Queued/Running/Completed/Stopped/Failed`, compteurs en direct, coût et plafond, étapes) ; les lancements en ligne de commande y figurent aussi |
| `POST /admin/bootstrap-runs/{id}/cancel` | — | demande l'arrêt avant le prochain lieu (`Stopped`, `outcome = cancelled`) ; 409 `already_finished` |
| `GET /admin/destinations` | — | destinations configurées |
| `GET /admin/places` | `destination`, `status?`, `limit` (50) | liste de lieux |
| `GET /admin/places/{id}` | — | lieu, intérêts, éthique, affluence |
| `POST /admin/places/{id}/publish`, `/unpublish` (`{ reason }`), `/reject` | — | version ou `rejected` |
| `PUT /admin/places/{id}/editorial` | `{ importanceOverride?, editoriallySaturated? }` | `updated` |
| `PUT /admin/places/{id}/ethics` | `{ fragile, accessRegulated }` | `updated` |
| `PUT /admin/places/{id}/interests` | `{ weights: { code: poids } }` | `updated` |
| `GET /admin/dedup`, `POST /admin/dedup/{id}/confirm`, `/revert` | `destination` | doublons proposés, fusion, annulation |
| `POST /admin/places/{id}/sources`, `/facts/extraction` | — | 202 |
| `GET /admin/places/{id}/facts`, `POST /admin/facts/{id}/decision` | `{ accept, reason? }` | faits, décision |
| `POST /admin/places/{id}/stories` | `{ lang, kind }` | 202 (rédaction) |
| `GET /admin/places/{id}/stories`, `GET /admin/stories?status=&limit=`, `GET /admin/stories/{id}` | — | histoires |
| `PUT /admin/stories/{id}/text` | `{ title, text }` | histoire |
| `POST /admin/stories/{id}/approve` (`{ editorialScore? }`), `/reject` (`{ reason }`), `/publish`, `/suspend` (`{ reason }`), `/resume`, `/correction` | — | histoire |
| `POST /admin/stories/{id}/audio` (202), `/audio/reset`, `PUT /admin/stories/{id}/voice` (`{ voice }`) | — | histoire |
| `GET /admin/reports?status=&kind=&limit=`, `POST /admin/stories/{id}/reports/resolve` | `{ status, note? }` | file des signalements (par histoire, avec le type de chaque remarque : `InaccurateFact`, `Pronunciation`, `ClosedOrMoved`, `Photo`, `Other` ; 400 si le type ou le statut est inconnu), clôture `Handled` ou `Dismissed` avec note de 300 caractères au plus |
| `POST /admin/batches` | `{ destination, minImportance?, placeStatuses?, lang?, kind?, limit?, budgetUsd? }` | 202 `{ id, total }` ; `budgetUsd` : plafond de coût estimé du lot (les tâches non démarrées sont alors annulées, `budget_exhausted`) |
| `GET /admin/batches`, `GET /admin/batches/{id}` | — | lots et détail : par lot `pending/running/succeeded/toReview/failed/cancelled`, `costUsd`, `status` (`running`, `completed`, `completed_with_failures`, `cancelled`) ; par tâche étape, essais, dernière erreur |
| `POST /admin/batches/{id}/retry`, `POST /admin/batches/{id}/cancel`, `POST /admin/batch-jobs/{id}/retry` | — | relance des tâches en échec ou annulées `{ requeued }`, annulation des tâches en attente `{ cancelled }` (409 `nothing_to_cancel`), relance d'une tâche (409 `not_retryable`) |
| `GET /admin/dead-letters?limit=` | — | messages abandonnés par la file après leurs essais, liés à leur lot et à leur lieu quand c'est une tâche de lot |
| `GET /admin/audit?limit=&actor=` | — | journal d'audit de Factory |
| `GET/PUT/DELETE /admin/pronunciations/{destination}/{term}` | `{ replacement }` | dictionnaire de prononciation |
| `GET /admin/videos/search?q=`, `GET/POST /admin/places/{id}/videos`, `DELETE /admin/places/{id}/videos/{videoId}` | `{ videoId }` | recherche YouTube côté serveur (seule route qui l'appelle), sélection |

## Back-office web (`web-admin`)

Application Blazor serveur, jamais appelée par l'app. Formulaires : `POST /login/code` (e-mail), `POST /login/verify` (e-mail + code), `POST /logout`. Le cookie `ov_admin` ne contient qu'un identifiant de session opaque ; les jetons Platform restent côté serveur, **en mémoire** : un redémarrage ferme les sessions. Pages : `/admin`, `/admin/places`, `/admin/workshop`, `/admin/batches`, `/admin/bootstrap`, `/admin/dead-letters`, `/admin/reports`, `/admin/references`, `/admin/config`, `/admin/audit`, `/admin/kpis`. `/health` et `/alive` sans authentification.

## Écarts avec le §12 du cahier des charges

Non encore implémentés : `GET /api/platform/v1/me/export` et `DELETE /me` (F-22, T-507), la recherche et les packs du Catalog (`/pois/nearby`, `/pois/bbox`, `/search`, `/packs`), `GET /me/candidates` et les recommandations côté serveur (T-502 à T-505), tous les endpoints Billing et Ads, et, côté Creators, les routes `studio` et `lists` (T-1206, T-1210). Les chemins du Catalog sont organisés par destination (`/destinations/{slug}/pois`) et non par `nearby`/`bbox`. La liste réelle fait foi.
