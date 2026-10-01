# ADR-0009 — Back-office Blazor (T-401, T-402, T-403, T-408)

- Statut : accepté. Couvre la coquille, la connexion par code e-mail, les lieux, l'atelier de contenu, la configuration, les flags et les référentiels. Hors périmètre : boîte des signalements (T-405), KPI (T-406), vidéos (T-407), projection d'audit dans Platform (T-409), suggestions de créateurs (T-410).

## Choix
- **`OnVoyage.Web.Admin`** : Blazor Web App en rendu **interactif serveur** (F-25). Il n'a ni base ni code métier : toutes les lectures et écritures passent par le **Gateway** avec le jeton de l'éditeur (`IAdminApi`). Les modèles de réponse sont des records locaux, sans référence aux couches Application des services.
- **Connexion** : le même code à six chiffres que les voyageurs, envoyé par Platform (ADR-0005). Deux formulaires HTML classiques (`/login/code`, `/login/verify`, avec anti-falsification) créent un cookie `HttpOnly`, `SameSite=Strict`, qui ne contient qu'un **identifiant de session opaque**. Les jetons Platform restent côté serveur (`AdminTokenStore`) et sont renouvelés à la demande (jeton de renouvellement à usage unique, un verrou évite deux renouvellements simultanés). Un redémarrage déconnecte tout le monde : acceptable pour une poignée d'éditeurs ; les clés de protection de données doivent être persistées pour plusieurs instances.
- **Contrôle d'accès** : `AdminGate` répond **403** et journalise (avertissement avec l'identifiant et le chemin) à tout compte connecté sans le rôle `admin` sur `/admin/*`, avant l'autorisation du routeur (sinon le cookie redirigerait vers une page de refus). Les pages portent en plus `[Authorize(Policy = "admin")]` et les services refusent eux-mêmes un jeton sans rôle (SEC-03) : le front n'est jamais la seule barrière.
- **Journal d'audit** : voir « Journal d'audit (T-409) » plus bas. Les modifications de configuration ont en plus leur historique versionné (`remote_config_history`), affiché dans l'écran Configuration.
- **Carte** : un SVG sans fond de carte. Aucune position ne sort vers un serveur de tuiles tiers ; une vraie carte (PMTiles auto-hébergées) viendra avec les packs hors ligne.
- **Diff** : comparaison au mot près (`WordDiff`, plus longue sous-suite commune) entre deux versions d'une histoire.

## Ajouts aux services pour l'atelier
Factory : catégories et poids saisis par l'éditeur (`PUT /places/{id}/interests`, niveau 1 déduit, jamais écrasés par un nouveau calcul, un lieu « à classer » devient candidat), drapeaux éthiques (`/ethics`), liste des destinations, histoires par statut, changement de voix et régénération de la voix (qui supprime l'audio déjà fait), lexique de prononciation, journal d'audit. Platform : liste des clés de configuration et historique d'une clé.

## Écarts et limites
- Les extraits d'onboarding (T-408) dépendent du service Discovery, qui n'existe pas encore : non faits. La taxonomie est en lecture seule (versionnée avec le code).
- La fusion de doublons se confirme depuis la liste des lieux ; l'annulation d'une fusion reste une action de l'API.
- Les écrans n'ont pas été vus dans un navigateur : ils sont vérifiés par des tests bUnit, par des tests d'intégration du parcours de connexion et des 403, et par la compilation. Un passage visuel (mise en page, accessibilité au clavier) reste à faire.

## Génération en masse (T-404)
Écran « Lots » : formulaire (destination, importance minimale, nombre de lieux, langue, type, statuts de lieu), liste des lots avec « N terminés · N échecs · N à relire » mise à jour toutes les 5 secondes tant qu'un lot tourne, détail par lieu (échecs en tête, lien vers l'histoire), « Relancer les échecs ». Voir ADR-0008 pour le fonctionnement des lots.

## Boîte des signalements (T-405)
- Chaque signalement a un statut (`Open`, `Handled`, `Dismissed`), une date et une note de traitement. L'écran « Signalements » regroupe par histoire, trie par date, par nombre de lecteurs ou par lieu, et n'affiche que le texte des remarques : jamais l'identité du lecteur.
- Actions : « Suspendre et ouvrir une correction » (histoire publiée) ou « Ouvrir une correction » (suspendue) — la correction est une nouvelle version à relire et ferme les signalements (« correction vN ») ; « Marquer traités » ; « Écarter » avec une note. Remettre une histoire en ligne écarte ses signalements. Le seuil de suspension (3 lecteurs différents) ne compte plus que les signalements **ouverts**, pour qu'un signalement écarté ne suspende pas de nouveau l'histoire.
- Écart : le cahier prévoit un type de signalement (`type`) et une table `error_report` liée au lieu ; nous gardons `story_report` (texte libre, lié à la version de l'histoire).

## Journal d'audit (T-409)
- Chaque écriture admin de Factory est consignée dans `factory.audit_log` et publiée (`AdminActionRecordedV1`, même transaction, outbox) vers Platform ; les écritures admin de Platform sont consignées directement. Platform tient la projection `platform.admin_audit` (idempotente sur l'identifiant de l'événement) et la sert à `GET /api/platform/v1/admin/audit` (filtre service, acteur). L'écran « Journal » la lit : tous les services, une seule liste.
- Chaque entrée dit qui (identifiant du compte), quoi (méthode et route), la cible, le code de réponse et un **résumé de la demande** (valeurs de route et corps, tronqués à 500 caractères).
- Limites : le résumé décrit ce qui a été demandé, pas l'état précédent (« avant ») ; le journal n'est pas purgé ni anonymisé à la suppression d'un compte administrateur ; le service Catalog n'a pas d'écriture admin. Les futurs services admin (Creators, Ads…) devront publier le même événement.

## Indicateurs (T-406)
- Écran « Indicateurs » : période, destination, cohorte (toutes, « Pour vous », témoin 20 %). Le **catalogue des indicateurs et leurs cibles (§26) vit dans l'Admin** (`KpiCatalog`) ; le service Insights n'a qu'à calculer des valeurs : `GET /api/insights/v1/kpis?from&to&destination&cohort` → `{ from, to, destination, cohort, values: { "<clé>": { value, sample } } }`. Les clés sont dans `KpiCatalog.All`.
- Chaque ligne dit « atteinte », « non atteinte », « suivi » (pas de cible), « pas de données » ou **« échantillon trop petit »** : l'hypothèse centrale n'est jugée qu'à partir de 1 000 impressions par cohorte, pour qu'un bon ratio sur peu de données ne passe pas pour une preuve. Le KPI stratégique vérifie que le taux de clic croît bien des profils maigres aux profils riches.
- **Insights (T-802) n'existe pas encore** : la route du Gateway (`/api/insights/v1/kpis`, rôle admin, cluster `insights-api`) est prête, mais sans service derrière elle l'écran affiche « Insights n'est pas encore branché » et liste tout de même les indicateurs. Il n'est donc testé que sur des données fixes (bUnit), comme le demande le cahier. L'export CSV est du ressort de T-802.

## Vidéos YouTube (T-407)
- **Recherche côté serveur seulement** (`GET /api/factory/v1/admin/videos/search`), avec la clé `YouTube:ApiKey` du serveur. La clé passe dans l'en-tête `x-goog-api-key`, pas dans l'adresse, car les journaux du client HTTP impriment l'adresse. Une recherche coûte 100 unités de quota : elle ne part que sur clic ou Entrée.
- L'éditeur choisit **0 à 2 vidéos par lieu** depuis la fiche du lieu. Le serveur ne croit pas la requête : il relit titre, chaîne et vignette chez YouTube à partir du seul identifiant (11 caractères, validé), refuse un doublon ou une troisième vidéo, **copie la vignette** dans notre stockage (`thumbs/{lieu}/{id}.jpg`; seulement `https`, hôtes d'images YouTube, JPEG, 1 Mo, sans redirection) puis, si le lieu est publié, le **republie** avec ses liens.
- `PoiPublishedV1` porte désormais `Links` (Wikipédia fr/en tirés de l'enrichissement, vidéos choisies) ; Catalog les remplace en bloc dans `external_link` et les expose dans `PoiDetailDto.Links` (vignette = notre copie servie sous `/media`). La fiche lieu affiche une section « En savoir plus » faite de **liens simples** (`target=_blank`, `rel=noopener noreferrer`) : aucune intégration, aucun appel à YouTube tant que le voyageur n'ouvre pas le lien.
- L'écran de l'éditeur affiche les vignettes des **résultats de recherche** depuis les serveurs d'images de YouTube (sans référent) : c'est l'éditeur, jamais un voyageur.
- Limites : le client YouTube n'a été essayé que sur des réponses simulées (réseau bloqué) ; la vignette d'une vidéo supprimée sur YouTube n'est pas retirée automatiquement ; `Media:PublicBaseUrl` doit pointer vers l'adresse publique du Gateway (`…/media`) pour que les apps résolvent les URL de vignettes et d'audio (le défaut `/media` est relatif au Catalog). Q-15 (conservation de vignettes) reste à trancher juridiquement.
