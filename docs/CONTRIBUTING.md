# Contribuer à ON.VOYAGE

Ce guide vaut pour les personnes comme pour les agents. Il résume les règles ; en cas de doute, les sources font foi : `.github/copilot-instructions.md` (architecture, style, vie privée, contenu IA), puis `docs/CAHIER_DES_CHARGES.md` (§0, §2, l'epic concernée, §23, la fiche de tâche du §24).

## Avant de commencer

1. Lire, dans l'ordre : `CLAUDE.md`, `.github/copilot-instructions.md`, le cahier des charges (§0 puis la tâche), `docs/adr/` et `docs/questions/`.
2. Préséance en cas de conflit (§0.2) : sur l'architecture et le style, les copilot-instructions l'emportent ; sur le fonctionnel, le légal et la vie privée, le cahier des charges l'emporte ; §2 (décisions arrêtées) l'emporte toujours.
3. Cloner, installer le SDK de `global.json` (.NET 10), Docker (tests d'intégration), puis suivre [README.md](../README.md) pour lancer l'ensemble avec Aspire. Les projets MAUI exigent des charges de travail à part : [MOBILE.md](MOBILE.md).

## Le travail, une tâche à la fois

- Une tâche du backlog (§24) = une branche `feat/T-xxx-…` = une PR. Commits au format **Conventional Commits** (`feat(catalog): …`, `fix(gateway): …`, `docs: …`, `ci: …`), petits et atomiques.
- Pas de développement direct sur `main`. La CI doit être verte avant la fusion ; `main` déclenche le déploiement du staging ([DEPLOYMENT.md](DEPLOYMENT.md)).
- **Doute bloquant** (exigence ambiguë, donnée voyageur vers un tiers non listé au §16.4, dépendance hors §10, règle impossible à respecter) : ne pas deviner. Écrire `docs/questions/Q-<date>-<sujet>.md` et passer à une tâche non bloquée.
- **Décision technique structurante** : une ADR (`docs/adr/NNNN-titre.md`, statut, contexte, décision, écarts au cahier).

## Définition de « terminé » (§0.3)

- [ ] Compile sans avertissement (`TreatWarningsAsErrors`).
- [ ] `dotnet test --solution OnVoyage.slnx` vert : unitaires, intégration, architecture, composants.
- [ ] Critères d'acceptation des exigences couverts par des tests automatisés ; sinon procédure manuelle dans `docs/testing/manual/`.
- [ ] `dotnet format OnVoyage.slnx --verify-no-changes` passe.
- [ ] Traces, métriques et journaux OpenTelemetry émis pour le nouveau flux.
- [ ] Documentation mise à jour (voir plus bas).
- [ ] Aucun secret, aucune clé, aucune donnée personnelle réelle commitée.
- [ ] La PR décrit la tâche, les exigences couvertes, les écarts, et joint des captures d'écran pour l'interface.

Commandes : `dotnet build OnVoyage.slnx`, `dotnet test --solution OnVoyage.slnx`, `dotnet format OnVoyage.slnx --verify-no-changes`, `dotnet run --project src/Aspire/OnVoyage.AppHost`. Détails des tests : [TESTING.md](TESTING.md).

## Architecture (vérifiée par les tests d'architecture)

- Un service = `Domain / Application / Infrastructure / Api` (+ `Contracts`, + `Worker` si besoin). `Api → Application → Domain` ; `Infrastructure → Application, Domain` (elle implémente les ports) ; `Api` ou `Worker` est la racine de composition et ne référence `Infrastructure` que dans `Program.cs`.
- Jamais d'accès à la base depuis `Api` ou `Application` ; `Domain` sans EF, Npgsql, OpenAI ni Wolverine.
- Pas de référence d'un service vers un autre, sauf vers ses `*.Contracts`. **Communication entre services par événements d'intégration Wolverine uniquement** ; les handlers d'événements vivent dans `Application/IntegrationEvents/`.
- Endpoints minces dans `Api/Endpoints/<Fonction>/` : liaison, envoi au bus, traduction du résultat ; aucune logique métier. Cas d'usage dans `Application/Features/<CasDUsage>/` (commande ou requête, handler, validateur, vue).
- Gateway = proxy technique pur : pas de logique métier, pas de schéma.
- Pas de « God service » ; aucun type nommé `*Helper(s)`, `*Utils`, `*Utility`, `*Manager` : nommer par responsabilité.
- CQRS et handlers avec **WolverineFx** (pas de MediatR). PostgreSQL + EF Core 10 ; une `DbContext` et un schéma par service.

## Style C#

`Nullable` activé, `sealed` par défaut, `record` pour DTO, commandes, requêtes et événements, constructeurs primaires pour l'injection, `CancellationToken` partout, `TimeProvider` injecté, UUID v7 (`Guid.CreateVersion7()`), erreurs métier attendues par un `Result` explicite (exceptions pour l'inattendu), Problem Details (RFC 9457) avec un `type` stable. Identifiants et commentaires en anglais ; textes d'interface en ressources FR/EN. L'analyse et le formatage sont dans `Directory.Build.props` et `.editorconfig` : le build et `dotnet format` les appliquent.

Blazor et MAUI : composants minces, état et règles dans `App.Core` ou les handlers ; la RCL `OnVoyage.UI.Components` ne référence pas MAUI ; CSS isolé par composant, thèmes clair et sombre, accessibilité (libellés, contrastes AA) ; seul JavaScript autorisé : `wwwroot/js/map.js` et ses bibliothèques embarquées.

## Vie privée (non négociable)

- Aucune donnée voyageur transmise à un tiers ; **aucun SDK d'analyse, de plantage, de publicité ou de paiement tiers** (Firebase, Google Analytics, Crashlytics, Sentry SaaS, Mixpanel, RevenueCat…). Un test d'architecture l'impose.
- **Aucune position stockée, journalisée ou tracée.** Les coordonnées ne passent que dans les paramètres de `GET …/pois` (et les routes de recommandations ou de surprise à venir). Pas de `lat`, `lon` dans un journal, une métrique, une étiquette ou une clé de cache (arrondi à 3 décimales).
- Déclenchement, rappels, visites et ciblage publicitaire personnalisé : calculés sur l'appareil.
- **Toute nouvelle donnée personnelle stockée est ajoutée à [PRIVACY.md](PRIVACY.md) dans la même PR**, avec sa durée de conservation, et à [DATABASE.md](DATABASE.md). Si les réponses des fiches stores en dépendent, mettre à jour `docs/stores/`.
- Pas d'iframe YouTube, Instagram ou TikTok, ni de ressource tierce dans le web ou l'app : lien sortant.

## Contenu généré par IA

Jamais d'appel LLM ou TTS sur le chemin d'une requête utilisateur ; rédaction en « monde fermé » à partir de faits extraits (le rédacteur ne reçoit jamais le texte source) ; prompts versionnés dans `prompts/` ; sorties structurées strictes ; version du prompt stockée avec chaque brouillon ; fournisseurs simulés et déterministes en test, aucun appel réseau externe en CI. Aucune donnée de voyageur dans un prompt.

## Ajouter ou modifier

### Un endpoint
Dans `Api/Endpoints/`, mince ; le cas d'usage dans `Application/Features/`. Si la route doit être joignable par les clients : ajouter la route YARP (`src/Gateway/OnVoyage.Gateway/appsettings.json`) avec sa politique. Mettre à jour [API.md](API.md).

### Une table ou une colonne
Modifier l'entité et la configuration EF dans `Infrastructure/Persistence`, puis générer la migration avec `dotnet ef migrations add <Nom>` (outil `dotnet-ef` à installer ; chaque projet Infrastructure a une fabrique de conception, donc pas de projet de démarrage à désigner), dans `Persistence/Migrations`. **Les migrations doivent rester rétrocompatibles d'une version** : elles s'appliquent au démarrage pendant que l'ancienne version tourne encore (§21) ; supprimer une colonne se fait en deux livraisons. Mettre à jour [DATABASE.md](DATABASE.md) (les tableaux de colonnes se régénèrent à partir de l'instantané du modèle) et PRIVACY.md si la donnée est personnelle.

### Un réglage
Lire la valeur par `IConfiguration` avec un défaut sûr ; une valeur modifiable à chaud va dans la configuration distante de Platform (`default-config.json`, annexe E). Aucun secret dans `appsettings.json`. Documenter dans [CONFIGURATION.md](CONFIGURATION.md), et dans `deploy/staging/` si c'est une variable d'environnement.

### Un événement d'intégration
Un enregistrement immuable `…V1` dans les `*.Contracts` du service émetteur ; route de publication (`PublishMessage<T>().ToPostgresqlQueue("<service>")`) ; handler idempotent côté consommateur dans `Application/IntegrationEvents/`. Mettre à jour [ARCHITECTURE.md](ARCHITECTURE.md).

### Un nouveau service
1. Projets `Domain`, `Application`, `Infrastructure`, `Api`, `Contracts` sous `src/Services/<Nom>/`, ajoutés à `OnVoyage.slnx`, avec `AddServiceDefaults()`, `AddOnVoyageAuthentication`, `AddOnVoyageMessaging` et un schéma propre.
2. `AppHost.cs` : ressource avec `WithReference(database)`, `Auth__JwtSecret`, contrôle de santé.
3. Gateway : route et cluster (`appsettings.json`).
4. Déploiement : bloc dans `deploy/staging/docker-compose.yml` (modèle déjà présent en commentaire), ligne de la matrice de `.github/workflows/deploy-staging.yml`, cible de sonde dans `deploy/staging/observability/prometheus.yml`, service dans le test de fumée si pertinent.
5. Documentation : ARCHITECTURE, DATABASE, API, CONFIGURATION, PRIVACY, tests d'architecture à jour.

## Documentation

Chaque PR met à jour les documents qu'elle rend inexacts (§22). Les affirmations doivent venir du code, pas de la spécification : vérifier avant d'écrire. Documents du dépôt : [README.md](../README.md), [ARCHITECTURE.md](ARCHITECTURE.md), [DATABASE.md](DATABASE.md), [API.md](API.md), [CONFIGURATION.md](CONFIGURATION.md), [TESTING.md](TESTING.md), [DEPLOYMENT.md](DEPLOYMENT.md), [PRIVACY.md](PRIVACY.md), [MOBILE.md](MOBILE.md), [runbooks/](runbooks/), [stores/](stores/). Restent à écrire (§22) : `DATA_SOURCES.md`, `LICENSING.md`, `AI_PIPELINE.md`, `RECOMMENDATION_ENGINE.md`, `TRIGGER_ENGINE.md`, `CREATORS.md`, et `docs/api/*.json` (OpenAPI).

## Exploitation

Staging : [DEPLOYMENT.md](DEPLOYMENT.md). Sauvegardes et restauration : [runbooks/restore.md](runbooks/restore.md). Observabilité : [runbooks/observability.md](runbooks/observability.md). Ne jamais corriger directement sur le serveur : tout passe par le dépôt et le workflow.
