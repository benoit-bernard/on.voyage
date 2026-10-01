# ADR-0009 — Back-office Blazor (T-401, T-402, T-403, T-408)

- Statut : accepté. Couvre la coquille, la connexion par code e-mail, les lieux, l'atelier de contenu, la configuration, les flags et les référentiels. Hors périmètre : génération en masse (T-404), boîte des signalements (T-405), KPI (T-406), vidéos (T-407), projection d'audit dans Platform (T-409), suggestions de créateurs (T-410).

## Choix
- **`OnVoyage.Web.Admin`** : Blazor Web App en rendu **interactif serveur** (F-25). Il n'a ni base ni code métier : toutes les lectures et écritures passent par le **Gateway** avec le jeton de l'éditeur (`IAdminApi`). Les modèles de réponse sont des records locaux, sans référence aux couches Application des services.
- **Connexion** : le même code à six chiffres que les voyageurs, envoyé par Platform (ADR-0005). Deux formulaires HTML classiques (`/login/code`, `/login/verify`, avec anti-falsification) créent un cookie `HttpOnly`, `SameSite=Strict`, qui ne contient qu'un **identifiant de session opaque**. Les jetons Platform restent côté serveur (`AdminTokenStore`) et sont renouvelés à la demande (jeton de renouvellement à usage unique, un verrou évite deux renouvellements simultanés). Un redémarrage déconnecte tout le monde : acceptable pour une poignée d'éditeurs ; les clés de protection de données doivent être persistées pour plusieurs instances.
- **Contrôle d'accès** : `AdminGate` répond **403** et journalise (avertissement avec l'identifiant et le chemin) à tout compte connecté sans le rôle `admin` sur `/admin/*`, avant l'autorisation du routeur (sinon le cookie redirigerait vers une page de refus). Les pages portent en plus `[Authorize(Policy = "admin")]` et les services refusent eux-mêmes un jeton sans rôle (SEC-03) : le front n'est jamais la seule barrière.
- **Journal d'audit** : toute écriture sur `/api/factory/v1/admin` est enregistrée dans `factory.audit_log` (acteur, méthode et route, cible, code de réponse) par un filtre d'endpoint ; les lectures non. Écran « Journal ». Le « avant/après » résumé et la projection dans Platform (`AdminActionRecordedV1`, T-409) restent à faire. Les modifications de configuration ont déjà leur historique versionné (`remote_config_history`), affiché dans l'écran Configuration.
- **Carte** : un SVG sans fond de carte. Aucune position ne sort vers un serveur de tuiles tiers ; une vraie carte (PMTiles auto-hébergées) viendra avec les packs hors ligne.
- **Diff** : comparaison au mot près (`WordDiff`, plus longue sous-suite commune) entre deux versions d'une histoire.

## Ajouts aux services pour l'atelier
Factory : catégories et poids saisis par l'éditeur (`PUT /places/{id}/interests`, niveau 1 déduit, jamais écrasés par un nouveau calcul, un lieu « à classer » devient candidat), drapeaux éthiques (`/ethics`), liste des destinations, histoires par statut, changement de voix et régénération de la voix (qui supprime l'audio déjà fait), lexique de prononciation, journal d'audit. Platform : liste des clés de configuration et historique d'une clé.

## Écarts et limites
- Les extraits d'onboarding (T-408) dépendent du service Discovery, qui n'existe pas encore : non faits. La taxonomie est en lecture seule (versionnée avec le code).
- La fusion de doublons se confirme depuis la liste des lieux ; l'annulation d'une fusion reste une action de l'API.
- Les écrans n'ont pas été vus dans un navigateur : ils sont vérifiés par des tests bUnit, par des tests d'intégration du parcours de connexion et des 403, et par la compilation. Un passage visuel (mise en page, accessibilité au clavier) reste à faire.
