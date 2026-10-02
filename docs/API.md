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
| `creators-studio-join` | `/api/creators/v1/studio/{registration|signup|terms}` | toutes | `account` (e-mail vérifié) | creators-api |
| `creators-studio` | `/api/creators/v1/studio/{**}` | toutes | `creator` | creators-api (dont `connections/{platform}/start|callback`, T-1207/1208) |
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

Créateur (espace `web-studio`, T-1206, préfixe `/studio`). Le créateur est **toujours le compte du jeton** : aucun identifiant de créateur dans les chemins, un identifiant d'un autre créateur est simplement inconnu (404). Un créateur suspendu lit mais n'écrit pas (403 `creator_suspended`) et ne peut pas lever sa suspension en republiant. Les écritures sont journalisées chez Platform comme celles de l'administrateur (acteur = compte du créateur).

| Méthode et chemin | Politique | Remarques |
| --- | --- | --- |
| `GET /registration` | `account` | `StudioRegistrationDto` : inscrit ou non, version courante des CGU, handle, statut, CGU acceptées. |
| `POST /signup` | `account` | `StudioSignupRequest` (`handle`, `displayName`, `acceptedTermsVersion`). Crée le brouillon et enregistre l'acceptation ; publie `CreatorTermsAcceptedV1` (Platform ajoute `creator`). 422 `terms_required` si la version n'est pas la version courante ; 409 `handle_taken` avec une suggestion. Idempotent. |
| `POST /terms` | `account` | Accepte la version courante (nouvelles CGU). Un fondateur garde son drapeau `founding`. |
| `GET` / `PUT /profile` | `creator` | `StudioProfileDto` (comme la fiche admin, sans identifiant de compte ni liste d'abonnés ; abonnés `null` sous 20). 409 `handle_locked` si le handle change sur un profil publié. |
| `POST /publish`, `/unpublish` | `creator` | Règle F-26 (`terms_required`, `specialty_required`). |
| `POST` / `PUT` / `DELETE /contents[/{contentId}]` | `creator` | Contenus par URL, chapitres, « Publicité ». |
| `POST` / `PUT` / `DELETE /place-links[/{linkId}]` | `creator` | Associations ; le créateur valide ou retire lui-même. |
| `PUT` / `DELETE /tips/{poiId}` | `creator` | Conseil de 280 caractères au plus. |
| `GET /places?query&destination` | `creator` | Recherche dans le répertoire. |
| `GET /place-links?status=proposed` | `creator` | `PlaceProposalsDto` (F-28) : « nous avons trouvé N lieux dans vos contenus », groupés par destination, meilleure confiance d'abord ; chaque proposition porte sa confiance (0–1), ses signaux (`text`, `chapter`, `context`, `partial`, `ambiguous`), sa preuve (extrait) et le lien vers le contenu à l'horodatage du chapitre. `readyCount` = propositions à `bulkThreshold` (0,9) ou plus ; `pending` = contenus pas encore analysés. Seul `proposed` est consultable (400 sinon). **Rien de cela n'est public.** |
| `POST /place-links/validate` | `creator` | `{ linkIds }` (une par une) ou `{ minConfidence }` (« Tout valider » : jamais en dessous du seuil `Creators:GeoAssociation:BulkValidateThreshold`, quelle que soit la valeur demandée). Seules les propositions de ce créateur sont atteintes ; chaque validation publie `CreatorPlaceLinkChangedV1`. |
| `POST /place-links/reject` | `creator` | `{ linkIds }` : refusée, elle n'est plus jamais reproposée. |
| `POST /place-links/{linkId}/correct` | `creator` | `{ poiId }` : la proposition est refusée et le lieu choisi est validé à sa place (même contenu, même chapitre). |
| `POST /place-links/analyze?force` | `creator` | 202 `AnalysisRequestedDto` : met en file l'analyse des contenus pas encore analysés (tous avec `force=true`). |
| `GET /connections` | `creator` | `ConnectionsDto` : pour `instagram` et `youtube`, `enabled` (faux tant que l'application de la plateforme est en revue, H-008) et le compte connecté (`ConnectedAccountDto` : nom, statut `active` ou `needs_reauth`, dernière synchronisation, nombre de contenus). **Jamais un jeton.** |
| `GET /connections/{platform}/start` | `creator` | `ConnectionStartDto.authorizeUrl` : l'adresse d'autorisation de la plateforme (code + PKCE ; l'`state` chiffré lie le créateur, la plateforme, le vérificateur PKCE et 10 minutes). 503 `connections_disabled` si la plateforme n'est pas ouverte. |
| `POST /connections/{platform}/callback` | `creator` | Corps `{ code, state }` : la page de retour du Studio y transmet ce que la plateforme a renvoyé, **avec le jeton du créateur**. 400 `invalid_state` (état inconnu, expiré, d'une autre plateforme ou d'un autre créateur), 422 `professional_account_required` (compte Instagram personnel) ou `access_denied`, 409 `account_in_use` (compte déjà connecté à un autre créateur), 502 `provider_error`. La première importation part en arrière-plan. |
| `DELETE /connections/{platform}?deleteContents` | `creator` | Révoque chez la plateforme quand elle le permet, supprime la ligne et donc les jetons, supprime les vignettes copiées ; `deleteContents=true` retire aussi les contenus importés (et publie les retraits de leurs lieux). 204. |
| `POST /sync` | `creator` | « Resynchroniser » : 202 `SyncRequestedDto` (nombre de comptes actifs mis en file). Un compte `needs_reauth` n'est pas relancé. |

Erreurs : Problem Details avec `type` = `https://on.voyage/problems/<code>` et l'extension `code`.

## Factory — `/api/factory/v1` (back-office)

Tout est en politique `admin`, sauf le signalement. Chaque écriture est consignée au journal d'audit de Factory (qui, quoi, cible, statut) et publiée à Platform (`AdminActionRecordedV1`). Les tâches longues sont postées sur la file `factory` du worker : la réponse est `202 Accepted`.

| Méthode et chemin | Corps / paramètres | Réponse |
| --- | --- | --- |
| `POST /stories/{id}/reports` | `{ reason }` — **politique `traveler`** (F-20) | `{ received: true }` |
| `POST /admin/imports`, `/admin/enrichments`, `/admin/scorings` | `{ destination }` | 202 |
| `POST /admin/snapshot-imports` | `{ destination }` | 202 : charge le snapshot versionné `data-pipeline/<destination>/` (idempotent, [ADR-0017](adr/0017-snapshot-et-amorcage-d-une-destination.md)) |
| `POST /admin/bootstrap` | `{ destination, maxPlaces?, minImportance?, lang?, budgetUsd, autoPublish?, forceImport?, skipImport? }` | 202 : amorçage de bout en bout plafonné par `budgetUsd` ([runbook](runbooks/bootstrap-marseille.md)) |
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
| `GET /admin/reports?status=&limit=`, `POST /admin/stories/{id}/reports/resolve` | `{ status, note? }` | file des signalements, clôture |
| `POST /admin/batches` | `{ destination, minImportance?, placeStatuses?, lang?, kind?, limit? }` | 202 `{ id, total }` |
| `GET /admin/batches`, `GET /admin/batches/{id}`, `POST /admin/batches/{id}/retry` | — | lots, détail, relance des tâches en échec |
| `GET /admin/audit?limit=&actor=` | — | journal d'audit de Factory |
| `GET/PUT/DELETE /admin/pronunciations/{destination}/{term}` | `{ replacement }` | dictionnaire de prononciation |
| `GET /admin/videos/search?q=`, `GET/POST /admin/places/{id}/videos`, `DELETE /admin/places/{id}/videos/{videoId}` | `{ videoId }` | recherche YouTube côté serveur (seule route qui l'appelle), sélection |

## Back-office web (`web-admin`)

Application Blazor serveur, jamais appelée par l'app. Formulaires : `POST /login/code` (e-mail), `POST /login/verify` (e-mail + code), `POST /logout`. Le cookie `ov_admin` ne contient qu'un identifiant de session opaque ; les jetons Platform restent côté serveur, **en mémoire** : un redémarrage ferme les sessions. Pages : `/admin`, `/admin/places`, `/admin/workshop`, `/admin/batches`, `/admin/reports`, `/admin/references`, `/admin/config`, `/admin/audit`, `/admin/kpis`. `/health` et `/alive` sans authentification.

## Espace créateur (`web-studio`)

Application Blazor serveur, jamais appelée par l'app. Même connexion que le back-office (`POST /login/code`, `/login/verify`, `/logout`, cookie `ov_studio` avec un identifiant de session opaque, jetons et **rôles** gardés côté serveur : le rôle `creator`, donné par Platform un instant après l'inscription, apparaît dès le renouvellement de la session). Pages : `/studio` (accueil), `/studio/join` (inscription et CGU), `/studio/profile`, `/studio/contents`, `/studio/tips`, `/studio/review` (propositions de lieux à valider, F-28), `/studio/connections` et `/studio/connections/{platform}/callback` (page où la plateforme renvoie le navigateur : elle remet `code` et `state` au service Creators, une seule fois). Tout `/studio/*` répond **403** à un compte sans le rôle `creator`, sauf `/studio` et `/studio/join`. `/health` et `/alive` sans authentification.

## Écarts avec le §12 du cahier des charges

Non encore implémentés : `GET /api/platform/v1/me/export` et `DELETE /me` (F-22, T-507), la recherche et les packs du Catalog (`/pois/nearby`, `/pois/bbox`, `/search`, `/packs`), `GET /me/candidates` et les recommandations côté serveur (T-502 à T-505), tous les endpoints Billing et Ads, et, côté Creators, les routes `studio` et `lists` (T-1206, T-1210). Les chemins du Catalog sont organisés par destination (`/destinations/{slug}/pois`) et non par `nearby`/`bbox`. La liste réelle fait foi.
