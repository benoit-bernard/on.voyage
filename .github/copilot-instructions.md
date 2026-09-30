# Instructions Copilot / agents — ON.VOYAGE

> Fichier à placer dans `.github/copilot-instructions.md`. Il complète `.github/instructions/architecture-governance.instructions.md` (copie du dépôt `engawa`) et les instructions `legacy-*` issues de SoWi, FlatLedger et Bébé en route, à fusionner ici. En cas de conflit sur l'architecture ou le style, ces instructions l'emportent sur le cahier des charges ; sur le fonctionnel, le légal et la vie privée, le cahier des charges l'emporte.

## Contexte

ON.VOYAGE est un guide de voyage audio géolocalisé qui apprend les goûts du voyageur et privilégie les lieux moins fréquentés. La source de vérité est `docs/CAHIER_DES_CHARGES.md`. Lis sa §0 avant toute tâche.

## Stack

- .NET 10, C# 14, Aspire (AppHost + ServiceDefaults), ASP.NET Core Minimal APIs.
- Modular Monolyth with kind of Microservices (vertical slices) : Platform, Catalog, Discovery, Factory, Insights, Billing, Ads, Creators ; Gateway YARP = proxy technique pur (aucune logique métier, aucun schéma).
- CQRS, handlers et messagerie : **WolverineFx** (transport et outbox PostgreSQL). Pas de MediatR.
- PostgreSQL (Supabase auto-hébergé) avec PostGIS, pgvector, unaccent, pg_trgm ; EF Core 10 + Npgsql + NetTopologySuite + Pgvector ; une `DbContext` et un schéma par service.
- Auth : Supabase Auth, session anonyme + OTP e-mail uniquement.
- IA : `Microsoft.Extensions.AI` + OpenAI, sorties structurées ; **uniquement dans Factory et le worker Creators (géo-association), en tâche de fond**, jamais avec des données voyageur.
- Mobile : .NET MAUI 10 Blazor Hybrid ; `OnVoyage.App.Core` pur et testable ; carte MapLibre GL JS + PMTiles ; GPS : `Geolocation` de MAUI au premier plan (MVP-0), Shiny.Locations en arrière-plan (MVP) ; audio CommunityToolkit MediaElement ; SQLite.
- Web : Blazor Web App (public en SSR statique, admin et espace créateur `Web.Studio` en interactif serveur) ; RCL partagée `OnVoyage.UI.Components`.
- Tests : xUnit v3, Shouldly, NSubstitute, Testcontainers, bUnit, Verify, FsCheck, ArchUnitNET.

## Architecture (obligatoire)

- Un microservice = une unité fonctionnelle isolée, découpé en `Domain / Application / Infrastructure / Api` (+ `Contracts`).
- Le fichier Architecture Governance (engawa) écrit « API → Application → Domain → Infrastructure (jamais l'inverse) ». Dans ce dépôt, cette règle s'applique **avec inversion de dépendance**, et cette précision l'emporte : `Api → Application → Domain` ; `Infrastructure → Application, Domain` (implémente les ports) ; `Api` (ou `Worker`) est la racine de composition et ne référence `Infrastructure` que dans `Program.cs` pour l'injection.
- Jamais d'accès à la base depuis `Api` ou `Application`.
- Pas de référence d'un service vers un autre, sauf vers ses `*.Contracts` ; les handlers d'événements d'intégration vivent dans `Application/IntegrationEvents/`.
- Communication entre services : événements d'intégration Wolverine uniquement, jamais d'appel HTTP synchrone de service à service.
- Endpoints dans `Api/Endpoints/<Feature>/`, minces : liaison, envoi au bus, traduction du résultat. Aucune logique métier dans un endpoint ou un DTO.
- Cas d'usage dans `Application/Features/<UseCase>/` : commande ou requête, handler, validateur, vue.
- Mapping strict Domain ↔ Infrastructure.
- `Domain` ne dépend d'aucun fournisseur (EF, Npgsql, OpenAI, Supabase, Wolverine).
- Pas de « God services ». Aucun type nommé `*Helper(s)`, `*Utils`, `*Utility`, `*Manager` : nomme par responsabilité.
- Tout nouveau flux émet traces, métriques et journaux OpenTelemetry.
- Tout nouveau code s'intègre sans rupture : build, tests et contrats existants restent verts.

## Style C#

- `Nullable` activé, `TreatWarningsAsErrors`, `sealed` par défaut, `record` pour DTO, commandes, requêtes et événements.
- Primary constructors pour l'injection ; `CancellationToken` partout ; `TimeProvider` injecté ; UUID v7.
- Erreurs métier attendues via un `Result` explicite ; exceptions réservées à l'inattendu.
- Problem Details (RFC 9457) pour les erreurs HTTP, avec un `type` stable.
- Identifiants et commentaires en anglais ; textes d'interface en ressources FR/EN.

## Blazor et MAUI

- Composants minces ; état et règles dans `App.Core` ou dans les handlers.
- La RCL `OnVoyage.UI.Components` ne référence pas MAUI.
- CSS isolé par composant ; thèmes clair et sombre ; accessibilité (libellés, contrastes AA).
- Seul JavaScript autorisé : `wwwroot/js/map.js` (MapLibre + PMTiles) et ses bibliothèques vendorisées.

## Vie privée (non négociable)

- Aucune donnée voyageur transmise à un tiers. Aucun SDK d'analytics, de crash, de publicité ou de paiement tiers (Firebase, Google Analytics, Crashlytics, Sentry SaaS, Mixpanel, RevenueCat…).
- Aucune position stockée, journalisée ou tracée côté serveur. Les coordonnées ne transitent que dans les paramètres de `nearby`, `bbox`, `recommendations` et `surprise` ; l'instrumentation OpenTelemetry expurge `url.query` et `url.full` ; les clés de cache arrondissent les coordonnées à 3 décimales.
- Déclenchement, rappels, visites et ciblage publicitaire personnalisé sont calculés sur l'appareil.
- Toute nouvelle donnée personnelle stockée est ajoutée au registre de `docs/PRIVACY.md` dans la même PR.
- Pas d'iframe YouTube, Instagram ou TikTok, ni de ressource tierce sur le web ou dans l'app : les contenus de créateurs s'ouvrent par lien sortant.
- Connexions Instagram, YouTube, TikTok : OAuth côté serveur uniquement, dans le service Creators ; jetons chiffrés par Data Protection, jamais journalisés ni renvoyés.
- Les follows ne sont jamais exposés aux créateurs ; statistiques créateur agrégées avec seuil k = 20.

## Contenu IA

- Jamais d'appel LLM ou TTS sur le chemin d'une requête utilisateur.
- Rédaction en « monde fermé » à partir des faits extraits ; le rédacteur ne reçoit jamais le texte source.
- Prompts versionnés dans `prompts/` ; sorties structurées strictes ; version du prompt stockée avec chaque brouillon.
- Tests : fournisseurs LLM et TTS simulés et déterministes ; aucun appel réseau externe en CI.

## Méthode de travail

- Une tâche du backlog (§24) = une branche `feat/T-xxx-…` = une PR, Conventional Commits.
- Définition de « terminé » : cahier des charges §0.3.
- Doute bloquant : écrire `docs/questions/Q-<date>-<sujet>.md`, puis passer à une tâche non bloquée. Ne jamais ajouter une dépendance hors de la liste §10 sans question.
- Commandes : `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`, `aspire run`.
