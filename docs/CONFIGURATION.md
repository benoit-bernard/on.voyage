# Configuration — ON.VOYAGE

Liste des réglages **réellement lus par le code** au 2026-10-01 (recherche de `configuration[…]`, `GetValue`, `GetSection`, `GetConnectionString` dans `src/`), avec leur valeur par défaut dans le code. Sources de configuration .NET habituelles : `appsettings.json`, `appsettings.{Environnement}.json`, variables d'environnement (`__` remplace `:` : `Auth:JwtSecret` devient `Auth__JwtSecret`), secrets utilisateur en développement. Aucun secret n'est commité (test d'architecture `Production_configuration_holds_no_secret`).

Trois autres jeux de réglages existent et ne sont **pas** dans ce document : la configuration distante de Platform (clés de l'annexe E, voir la dernière section), les paramètres de l'AppHost Aspire, les variables du déploiement ([DEPLOYMENT.md](DEPLOYMENT.md), `deploy/staging/.env.example`).

## Commun à tous les services

| Clé | Défaut | Rôle |
| --- | --- | --- |
| `ConnectionStrings:onvoyage` | aucun (obligatoire pour tout service qui a une base) | Chaîne Npgsql de la base unique. En développement, Aspire l'injecte (`WithReference(database)`). Factory l'utilise aussi pour lancer `osm2pgsql` (hôte, port, utilisateur, base, mot de passe). |
| `Auth:JwtSecret` | aucun ; **32 octets minimum**, sinon le démarrage échoue | Clé HS256 commune : Platform signe, Gateway et services valident. Platform en dérive aussi le « poivre » des empreintes de codes. Obligatoire dans Gateway, Platform, Catalog, Discovery, Factory.Api (pas dans Factory.Worker ni Web.Admin). En développement : `appsettings.Development.json` fournit une valeur de développement **qui ne doit jamais servir ailleurs**. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | vide : aucun export | Active l'exportation OTLP des traces, métriques et journaux (`ServiceDefaults`). Les autres variables standard `OTEL_*` du SDK (`OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`, `OTEL_EXPORTER_OTLP_PROTOCOL`) s'appliquent. |
| `Logging:LogLevel:*` | `Information` (`Warning` pour `Microsoft.AspNetCore` et la commande SQL d'EF) | Niveaux de journal. Les catégories qui impriment l'URL de la requête (`Microsoft.AspNetCore.Hosting.Diagnostics`, `Yarp.ReverseProxy.Forwarder.HttpForwarder`, `System.Net.Http.HttpClient`, `Microsoft.AspNetCore.HttpLogging`) sont **plafonnées à `Warning` dans le code** et ne peuvent pas être relevées par la configuration (la position ne doit jamais être journalisée). |
| `ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_URLS`/`ASPNETCORE_HTTP_PORTS`, `AllowedHosts` | `Production` ; `*` | Réglages ASP.NET Core standard. Les images Docker écoutent sur 8080. |

## Platform (`platform-api`)

| Clé | Défaut | Rôle |
| --- | --- | --- |
| `Platform:Migrate` | `true` | Applique les migrations EF au démarrage. |
| `Platform:SeedDefaults` | `true` | Charge l'annexe E et les drapeaux dans une base vide. |
| `Platform:RepublishOnStart` | `true` | Republie la version courante de chaque clé de configuration (`ConfigChangedV1`) après le démarrage. |
| `Messaging:ConfigSubscribers` | `["catalog"]` | Files qui reçoivent `ConfigChangedV1`. |
| `Email:Provider` | `resend` (appsettings) ; seules valeurs acceptées : `resend`, ou `log` (refusé hors `Development` : il écrit les codes dans le journal) | Envoi du code de connexion. |
| `Email:Resend:ApiKey` | aucun | Clé API Resend (secret). |
| `Email:From` | `ON.VOYAGE <connexion@on.voyage>` (appsettings) | Expéditeur ; le domaine doit être vérifié chez Resend. |
| `Auth:RefreshTokenDays` | `90` | Durée du jeton de rafraîchissement. |
| `Auth:BootstrapAdminEmails` | `[]` | Adresses qui reçoivent le rôle `admin` à la connexion (`Auth__BootstrapAdminEmails__0`, `__1`…). |

La durée de vie du jeton d'accès, les délais et plafonds des codes (`auth.otp_ttl_minutes`, `otp_max_attempts`, `otp_resend_seconds`, `access_token_minutes`) et `security.otp_per_email_per_hour` viennent de la **configuration distante**, pas d'ici.

## Catalog (`catalog-api`)

| Clé | Défaut | Rôle |
| --- | --- | --- |
| `Catalog:Migrate` | `true` | Migrations au démarrage. |
| `Catalog:SeedDemoData` | `false` | Charge le petit jeu de démonstration interne de Catalog (tests, staging historique). **Ne pas l'activer avec le snapshot** : ses identifiants sont différents et les lieux seraient en double. |
| `Media:RootPath` | vide : `/media` n'est pas servi | Dossier des fichiers audio, servi sous `/media`. Doit exister au démarrage. En staging : volume partagé avec le worker Factory (`/media`). |
| `Media:PublicBaseUrl` | `/media` | Préfixe des adresses audio renvoyées au client. Doit être **publique** et absolue pour une app mobile (`https://<domaine>/media`). |

## Discovery (`discovery-api`)

| Clé | Défaut | Rôle |
| --- | --- | --- |
| `Discovery:Migrate` | `true` | Migrations au démarrage. |
| `Media:PublicBaseUrl` | `/media` | Préfixe des adresses audio des extraits d'onboarding. |
| `Discovery:AllowTextOnlyStories` | `true` | `GET /me/candidates` propose aussi les lieux dont l'histoire est publiée **sans audio** (marquée `textOnly`) : l'appareil lit le texte avec sa voix (ADR-0018). `false` : histoires enregistrées seulement. |

## Creators (`creators-api`)

| Clé | Défaut | Rôle |
| --- | --- | --- |
| `Creators:Migrate` | `true` | Migrations au démarrage. |
| `Creators:Terms:CurrentVersion` | `2026-10` | Version des CGU créateurs que l'inscription (`POST /studio/signup`) exige. La changer fait redemander l'acceptation (`POST /studio/terms`). Le texte juridique (H-010) est dans l'espace créateur ; la version est ici. |
| `Creators:GeoAssociation:Provider` | `disabled` | Qui lit les lieux cités par les contenus (F-28) : `disabled` (rien n'est analysé, rien n'échoue), `offline` (déterministe, sans réseau : tests, démonstration) ou `openai` (modèle de langage, **en arrière-plan seulement**, jamais sur le chemin d'une requête, jamais avec des données de voyageur). `openai` exige `OpenAI__ApiKey` et `Creators__Llm__GeotagModel` (**secrets / paramètres** ; modèle à fixer, Q-14) : le service refuse de démarrer sans eux. |
| `Creators:GeoAssociation:BulkValidateThreshold` | `0.9` | Confiance à partir de laquelle « Tout valider » s'applique (annexe E : `creators.geotag.auto_validate_threshold`). Plancher 0,5. |
| `Creators:GeoAssociation:MinProposalConfidence` | `0.4` | En dessous, un rapprochement n'est pas proposé. |
| `Creators:GeoAssociation:MaxProposalsPerContent` | `50` | Plafond de propositions pour un contenu. |
| `Creators:GeoAssociation:SuggestionConfidence` | `0.7` | Certitude du lecteur (qu'il s'agit d'un vrai lieu) à partir de laquelle un lieu inconnu du catalogue est suggéré à l'équipe éditoriale (`PlaceSuggestedV1`). |
| `Creators:Social:Provider` | `live` | `live` (les plateformes) ou `fake` (adaptateurs déterministes hors ligne : tests, démonstration sans identifiants). |
| `Creators:Social:Instagram:Enabled`, `Creators:Social:YouTube:Enabled` | `false` | **Import désactivé par défaut** (H-008 : revue des applications Meta et Google en attente). Activer exige aussi les trois clés suivantes, sinon la plateforme reste fermée pour les créateurs (« pas encore ouvert »). |
| `Creators:Social:<Plateforme>:ClientId`, `…:ClientSecret` | aucun | **Secrets** : identifiants de l'application chez Meta (Instagram API with Instagram Login) et Google (client OAuth « application web », portée `youtube.readonly`). Variables d'environnement `Creators__Social__Instagram__ClientId`, `Creators__Social__Instagram__ClientSecret`, `Creators__Social__YouTube__ClientId`, `Creators__Social__YouTube__ClientSecret` ; jamais dans le dépôt. |
| `Creators:Social:<Plateforme>:RedirectUri` | aucun | Adresse de retour déclarée chez la plateforme : **une page de l'espace créateur**, `https://studio.<domaine>/studio/connections/instagram/callback` et `…/youtube/callback`. |
| `Creators:Social:<Plateforme>:UsePkce` | `true` | Envoie un défi PKCE S256. Google le supporte ; la prise en charge par Instagram n'est pas documentée : mettre `false` si l'autorisation est refusée à cause du paramètre. |
| `Creators:Social:Instagram:ApiVersion` | vide | Préfixe de version du Graph API (`v23.0`) ; vide : adresse sans version. |
| `Creators:DataProtection:KeysDirectory` | aucun | Dossier des **clés qui chiffrent les jetons OAuth** (ASP.NET Core Data Protection). **Obligatoire dès qu'une plateforme est activée** (le service refuse de démarrer sinon) et à garder entre deux déploiements (volume Docker, sauvegardé) : sans ces clés, les jetons stockés sont illisibles et les créateurs doivent reconnecter leurs comptes. |
| `Creators:DataProtection:CertificatePath`, `…:CertificatePassword` | aucun | Certificat PKCS#12 qui chiffre les clés au repos (sinon elles sont en clair dans le dossier : le dossier doit alors être protégé comme un secret). |
| `Creators:Social:MediaDirectory` | aucun | Racine des médias (celle que Catalog sert sous `/media`) où les vignettes des contenus importés sont copiées, dans `creators/`. Vide : aucune vignette copiée. L'AppHost y met le dossier média partagé. |
| `Creators:Social:ThumbnailHosts` | `i.ytimg.com`, `img.youtube.com`, `*.cdninstagram.com`, `*.fbcdn.net` | Hôtes d'où une vignette peut être téléchargée (https, 1 Mo, image, sans redirection). |
| `Creators:Social:SyncEnabled`, `Creators:Social:SyncIntervalHours` | `false`, `24` | Synchronisation incrémentale quotidienne dans le processus `creators-api` (pas de worker Creators séparé pour l'instant). |
| `Messaging:CreatorSubscribers` | `["discovery"]` | Files qui reçoivent `CreatorPublishedV1`, `CreatorUnpublishedV1`, `CreatorPlaceLinkChangedV1` et `FollowChangedV1`. Discovery les traite depuis T-1205 ; **n'ajouter `insights` qu'avec ses gestionnaires** (T-1212), sinon les messages seraient mis de côté. `[]` désactive le routage. `CreatorTermsAcceptedV1`, le journal admin et les réponses aux droits des données vont toujours à `platform`. |
| `Messaging:ProjectionSubscribers` (lue par **Catalog**) | `["creators", "discovery"]` | Files qui reçoivent `PoiProjectionChangedV1`. |
| `Exports:Directory` | dossier temporaire | Dossier de la partie `creators.json` des exports (comme les autres services). |

## Site public (`web-public`)

| Clé | Défaut | Rôle |
| --- | --- | --- |
| `Catalog:BaseAddress` | `https+http://catalog-api` | Catalog, lu avec un jeton `internal`. |
| `Creators:BaseAddress` | `https+http://creators-api` | Creators (pages `/@handle`, bloc des lieux, sitemap), lu avec le même jeton. Seules les lectures publiques (`traveler_or_internal`) lui sont ouvertes. |
| `Public:MediaBaseUrl` | `https://on.voyage/media` | Adresse publique de nos médias (avatars des créateurs, JSON-LD `Person`). |

## Factory (`factory-api` et `factory-worker`)

Les deux hôtes lisent la même section ; le worker exécute les tâches, l'API enregistre les commandes.

| Clé | Défaut | Rôle |
| --- | --- | --- |
| `Factory:Migrate` | `true` | Migrations au démarrage (API et worker). |
| `Factory:Destinations` | Marseille dans `appsettings.json` | Liste de destinations : `Slug`, `Name`, `CenterLatitude`, `CenterLongitude`, `BoundingBox` (`[minLon, minLat, maxLon, maxLat]`, obligatoire), `OsmExtractUrl` (défaut : extrait Geofabrik PACA), `OsmExtractFile` (fichier local, évite le téléchargement). Ouvrir une destination est un changement de configuration, pas une livraison (D-13). |
| `Factory:ClassificationRulesPath` | règles embarquées (`mappings.json`) | Fichier de règles de classification d'un éditeur. |
| `Factory:Heritage` | UNESCO `Q9259`, classé `Q10387689`, inscrit `Q10387575` | Classes Wikidata du patrimoine (`Unesco`, `Classified`, `Inscribed`). |
| `Factory:Content` | voir ci-dessous | Réglages de la rédaction (`ContentSettings`) : `MinFacts` 3, `WriterAttempts` 2, `ReportSuspendThreshold` 3, `ReportsPerTravelerPerDay` 10, `DefaultVoiceFr` `marin`, `DefaultVoiceEn` `cedar`, `SpeechInstructions`, `SourceQuality` (`official` 1,0 ; `merimee` 0,95 ; `wikipedia` et `wikidata` 0,7 ; `other` 0,5). |
| `Factory:DataDirectory` | `<tmp>/onvoyage-factory` | Dossier de travail (extraits OSM téléchargés, style Lua). En staging : `/data/factory` (volume). |
| `Factory:MediaDirectory` | `<DataDirectory>/media` | Où le worker écrit l'audio. Doit désigner le même dossier que `Media:RootPath` de Catalog. |
| `Factory:Osm:Osm2pgsqlPath` | `osm2pgsql` (PATH) | Binaire d'import. |
| `Factory:Osm:TimeoutMinutes` | `60` | Délai maximal d'un import. |
| `Factory:Osm:ReuseDownloadDays` | `1` | Un extrait téléchargé depuis moins de ce nombre de jours est réutilisé. |
| `Factory:Audio:FfmpegPath` | `ffmpeg` (PATH) | Normalisation audio. |
| `Factory:Audio:LoudnessLufs` | `-16` | Niveau sonore cible. |
| `Factory:Audio:BitrateKbps` | `48` | Débit du MP3 produit. |
| `Factory:Jobs:MaxParallel` | `4` | Tâches simultanées du worker (borne les appels au fournisseur payant). |
| `Factory:Retry:DelaysSeconds` | `[10, 60, 300]` | Délais de réessai en cas de panne d'un fournisseur, avant la lettre morte. |
| `Factory:Llm:Provider` | `openai` ; `disabled` (aucun modèle, développement) ; `offline` (adaptateurs déterministes pour l'extraction, la rédaction et la vérification, texte Wikipédia servi par le snapshot : voir [ADR-0017](adr/0017-snapshot-et-amorcage-d-une-destination.md)) | Avec `openai`, le démarrage **échoue** si une des clés suivantes est vide. |
| `OpenAI:ApiKey` | aucun | Clé API OpenAI (secret). Exigée avec le fournisseur `openai`. |
| `Factory:Llm:ExtractorModel`, `WriterModel`, `VerifierModel`, `ClassifierModel` | aucun | Identifiants des modèles de chaque rôle (exigés avec `openai`). |
| `Factory:Llm:Pricing:<modèle>:InputPerMillion`, `OutputPerMillion`, `PerMillionCharacters` | `0` | Prix en USD pour le calcul du coût (`onvoyage.llm.cost_usd`, table `factory.llm_call`). Clé hiérarchique : le nom du modèle sert de segment (éviter les points dans le nom côté variables d'environnement). |
| `Factory:Tts:Model` | `gpt-4o-mini-tts` | Modèle de synthèse vocale. |
| `Factory:Tts:Provider` | `auto` : `openai` avec le modèle OpenAI, sinon `espeak` | `openai`, `espeak` (`espeak-ng` en développement, voix robotique), `none`. Sans voix disponible, les histoires du snapshot et de l'amorçage sont publiées **sans audio**. |
| `Factory:Tts:EspeakPath` | `espeak-ng` (PATH) | Binaire `espeak-ng`. |
| `Factory:Snapshot:Directory` | recherche de `data-pipeline/` depuis le dossier courant vers le haut | Racine du snapshot de contenu (`<racine>/<destination>/destination.json` et `pois.json`). |
| `Factory:Snapshot:ImportOnStart` | `false` (le lanceur `scripts/dev-local.sh` le met à `true`) | Le worker importe le snapshot de chaque destination après son démarrage (idempotent). |
| `Factory:Snapshot:Destinations` | toutes les destinations configurées | Limite l'import au démarrage à ces slugs. |
| `Factory:Offline:Destination` | `marseille` | Destination dont le snapshot sert de « Wikipédia » au fournisseur `offline`. |
| `Bootstrap:*` (ligne de commande du worker) | voir [runbooks/bootstrap-marseille.md](runbooks/bootstrap-marseille.md) | `MaxPlaces`, `MinImportance`, `Lang`, `BudgetUsd`, `AutoPublish`, `ForceImport`, `SkipImport`, `AllowUnpriced`, `PauseMilliseconds`, `RetryDelaysSeconds`. Lus seulement par `dotnet run --project …Factory.Worker -- bootstrap <destination>`. |
| `YouTube:ApiKey` | aucun | Clé de l'API YouTube Data pour la recherche de vidéos par l'éditeur ; absente, la recherche répond `503` (`youtube_not_configured`). |

## Gateway

| Clé | Défaut | Rôle |
| --- | --- | --- |
| `ReverseProxy:Routes`, `ReverseProxy:Clusters` | voir `appsettings.json` | Routes et clusters YARP (voir [API.md](API.md)). Les destinations utilisent la découverte de services : `https+http://catalog-api`… |
| `services:<nom>:http:0` | aucun | Adresse d'un service (`services__catalog-api__http__0=http://catalog-api:8080`) ; Aspire l'injecte, Docker Compose le fournit à la main. |
| `Gateway:PlatformBaseAddress` | `http://platform-api` | Où lire les réglages de protection (`scope=edge`). |
| `Gateway:EdgeConfigRefreshSeconds` | valeur distante `security.edge_config_refresh_seconds` (60) | Force la période de relecture. |
| `Gateway:TrustForwardedHeaders` | `false` | À `true` derrière Caddy/Traefik : adresse client lue dans `X-Forwarded-For`, un seul saut de confiance. À laisser à `false` sinon (chacun choisirait son compartiment de limitation). |
| `Cors:AllowedOrigins` | `[]` (développement : `http://localhost:5090`) | Origines autorisées (`GET`, `POST`, `OPTIONS`). |

## Applications web

| Application | Clé | Défaut | Rôle |
| --- | --- | --- | --- |
| `web-admin` | `Gateway:BaseUrl` | `https+http://gateway` ; **obligatoire** | Adresse interne du Gateway. |
| `web-admin` | `Admin:MediaBaseUrl` | vide : repli sur `Gateway:BaseUrl` | Adresse que le **navigateur** de l'éditeur utilise pour lire l'audio (origine publique). |
| `web-studio` | `Gateway:BaseUrl` | `https+http://gateway` ; **obligatoire** | Adresse interne du Gateway (espace créateur, T-1206). Aucune base, aucun secret : le jeton est celui du créateur. |
| `web-pwa` | `Gateway:BaseAddress` | vide : même origine | Adresse du Gateway (`wwwroot/appsettings.json`, lu par le navigateur au démarrage : aucune injection par Aspire possible). |
| `web-pwa` | `Map:TilesUrl` | vide : la page Carte dit que la carte n'est pas configurée | Fichier `.pmtiles` servi avec requêtes Range. |
| `web-pwa` | `Map:GlyphsUrl` | `_content/OnVoyage.UI.Components/fonts/{fontstack}/{range}.pbf` | Polices de la carte (embarquées, pas de tiers). |

L'application mobile n'a pas de fichier de configuration : l'adresse du Gateway est fixée à la compilation dans `MauiProgram.cs` (`http://10.0.2.2:5080/` en Debug Android, `http://localhost:5080/` en Debug autre, `https://api.on.voyage/` en Release) ; les préférences (`Preferences`, `SecureStorage`) ne contiennent que des données d'usage (profil, drapeaux, session).

## Paramètres de l'AppHost (Aspire)

| Paramètre | Mode | Alimente |
| --- | --- | --- |
| `jwt-secret` | toujours (généré et gardé dans les secrets utilisateur en local) | `Auth__JwtSecret` |
| `resend-api-key` | publication seulement | `Email__Resend__ApiKey` ; en local, `Email__Provider=log` |
| `openai-api-key` | publication seulement | `OpenAI__ApiKey` (worker uniquement : voir l'écart signalé dans [DEPLOYMENT.md](DEPLOYMENT.md)) |
| `llm-extractor-model`, `llm-writer-model`, `llm-verifier-model`, `llm-classifier-model` | publication seulement | `Factory__Llm__*Model` |

Valeurs fixées par l'AppHost : `Catalog__SeedDemoData=false` et `Factory__Snapshot__ImportOnStart=true` (local : le catalogue vient du snapshot, via Factory), `Factory__Llm__Provider=offline` (local), `Media__RootPath` et `Factory__MediaDirectory` = `<tmp>/onvoyage-media`, `Gateway__TrustForwardedHeaders=true` (publication), `Gateway__BaseUrl` et `Admin__MediaBaseUrl` du back-office.

## Configuration distante (Platform, `platform.remote_config`)

Valeurs modifiables à chaud par un administrateur (`PUT /api/platform/v1/admin/config/{clé}`), versionnées et journalisées, propagées par `ConfigChangedV1`. Valeurs par défaut : `src/Services/Platform/OnVoyage.Platform.Infrastructure/Seed/default-config.json` (annexe E). Clés de premier niveau : `auth`, `onboarding`, `profile`, `cf`, `reco`, `ethics`, `trigger`, `reminders`, `audio`, `content`, `tts`, `data`, `ads`, `creators`, `billing`, `security`, `deletion`, `retention`, `cache`, `sync`, `app`.

Lues par le code aujourd'hui :

| Clé | Lue par | Effet |
| --- | --- | --- |
| `auth.otp_ttl_minutes` (10), `auth.otp_max_attempts` (5), `auth.otp_resend_seconds` (60), `auth.access_token_minutes` (60), `security.otp_per_email_per_hour` (5) | Platform | Connexion par code |
| `security.rate_per_traveler_per_min` (120), `security.rate_per_ip_per_min` (300), `security.blocked_user_agents`, `security.edge_config_refresh_seconds` (60) | Gateway | Protection |
| `app.min_app_version` (`1.0.0`) | Catalog (`426`) | Version minimale de l'app |
| `trigger.*` | app (`TriggerSettings.FromJson`) | Moteur de déclenchement |

Les autres clés (par exemple `retention.*`, `deletion.*`, `cache.*`, `sync.*`, `ads`, `billing`) sont présentes et versionnées mais **pas encore consommées** par du code. Drapeaux de fonctionnalité par défaut : `car_mode`, `background_discovery`, `automatic_audio`, `recommendations_cf`, `surprise_me`, `offline_packs`, `anecdotes`, `english`, `ads`, `kyutai_tts` désactivés ; `control_cohort` activé à 100 %.
