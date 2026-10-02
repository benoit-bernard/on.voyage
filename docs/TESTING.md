# Tests — ON.VOYAGE

État au 2026-10-01, vérifié dans `tests/`, `Directory.Packages.props` et `.github/workflows/`. Stratégie cible : §20 du cahier des charges ; ce document dit ce qui existe et comment le lancer.

## Outils réellement utilisés

| Besoin | Outil |
| --- | --- |
| Tests | xUnit v3 (`xunit.v3` 4.0), exécuteur **Microsoft.Testing.Platform** (`global.json`) |
| Assertions, doublures | Shouldly, NSubstitute (références ajoutées automatiquement à chaque projet de test par `tests/Directory.Build.targets`) |
| Base de données | Testcontainers PostgreSQL (`postgis/postgis:16-3.4`) ou un serveur existant (`ONVOYAGE_TEST_PG`) |
| Hôte HTTP en test | `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`), Wolverine réel sur PostgreSQL |
| Composants Razor | bUnit 2 |
| Temps | `Microsoft.Extensions.TimeProvider.Testing` et horloge virtuelle du moteur de déclenchement |
| Architecture | règles écrites à la main sur les fichiers `.csproj` et les sources (`XDocument`, recherche de texte) dans `tests/OnVoyage.ArchitectureTests` |

Le cahier des charges cite aussi FsCheck, Verify, ArchUnitNET et k6 : **aucun n'est dans le dépôt à ce jour**. Les « résultats approuvés » du moteur de déclenchement sont des fichiers JSON comparés par un petit outil maison (voir plus bas), pas Verify. Pas de test de charge (T-1101), pas de mesure de couverture.

## Lancer les tests

Prérequis : SDK .NET 10 (`global.json`), Docker pour les tests d'intégration (ou `ONVOYAGE_TEST_PG`), `osm2pgsql` pour les tests d'intégration de Factory (import réel sur un petit extrait). Voir aussi `CLAUDE.md`.

```bash
dotnet build OnVoyage.slnx -c Release
dotnet test --solution OnVoyage.slnx -c Release            # tout
dotnet test --project tests/OnVoyage.ArchitectureTests     # un seul projet (ici : l'architecture, 2 s)
dotnet format OnVoyage.slnx --verify-no-changes
```

`dotnet test` avec Microsoft.Testing.Platform prend `--solution` ou `--project` (et non un chemin positionnel). Les options propres à xUnit v3 (filtres par classe, méthode ou trait) se passent à l'exécuteur : `dotnet test --project … --help` les liste.

Sans Docker, pointer les tests d'intégration vers une instance PostgreSQL avec PostGIS, pg_trgm et unaccent, en **super-utilisateur** (chaque test crée sa propre base `catalog_test_<guid>`) :

```bash
export ONVOYAGE_TEST_PG="Host=localhost;Username=<superuser>;Password=<mot de passe>;Database=postgres"
dotnet test --solution OnVoyage.slnx -c Release
```

L'application mobile (`src/Mobile/OnVoyage.App`) n'est pas dans `OnVoyage.slnx` et n'a pas de test propre : sa logique est dans `App.Core`, `App.Infrastructure` et `App.LocalData`, testés sans MAUI.

## Projets de test

| Projet | Niveau | Ce qu'il couvre |
| --- | --- | --- |
| `OnVoyage.ArchitectureTests` | architecture | couches (Domain sans dépendance externe, Application sans Infrastructure ni Api, Infrastructure sans Api), références entre services uniquement par `*.Contracts`, endpoints et Application sans accès aux sources de données, paquets de base de données confinés à l'infrastructure, Gateway sans domaine, RCL et cœur client sans MAUI, moteur et taxonomie purs, **aucun SDK d'analyse, de plantage, de publicité ou de paiement tiers**, aucun secret dans la configuration de production, seul Platform émet des jetons, aucun nom `*Helper`, `*Utils`, `*Manager`, aucune ressource tierce dans le HTML client (14 tests) |
| `OnVoyage.Recommendation.Engine.Tests` | unitaire | apprentissage du profil (rejeu de l'historique, verrous, profondeur), classement, planificateur de visite |
| `OnVoyage.App.Core.Tests` | unitaire | moteur de déclenchement rejoué sur des traces GPX, contrôleur du mode découverte, lecteur audio (file, vitesses, interruptions), retours, rappels de proximité, carte, session |
| `OnVoyage.App.Infrastructure.Tests` | unitaire | client HTTP du Gateway |
| `OnVoyage.App.LocalData.Tests` | unitaire/intégration SQLite | `user.db`, file d'envoi, lecteur de packs |
| `OnVoyage.UI.Components.Tests` | composants (bUnit) | pages et composants de la RCL, carte avec interop JS simulée |
| `OnVoyage.Web.Admin.Tests` | composants (bUnit) | pages du back-office, KPI, différences de mots |
| `OnVoyage.Gateway.Tests` | intégration | JWT invalide (401), robots d'IA (403), dépassement de débit (429), traces sans coordonnées |
| `Platform.UnitTests`, `Platform.IntegrationTests` | unitaire, intégration | connexion par code, sessions, configuration, consentements, événements entre services |
| `Catalog.UnitTests`, `Catalog.IntegrationTests` | unitaire, intégration | requêtes `ST_DWithin`, API de lecture, projection de configuration |
| `Discovery.UnitTests`, `Discovery.IntegrationTests` | unitaire, intégration | validation des lots, onboarding, API d'interactions, schéma sans position |
| `Factory.UnitTests`, `Factory.IntegrationTests` | unitaire, intégration | classification, scores, contrôles de texte, pipeline de lieux et de contenu, lots, journal d'audit |
| `OnVoyage.TestInfrastructure` | support | `PostgresFixture` (un serveur PostGIS par exécution), jetons de test |

Environ 565 tests (`[Fact]` et `[Theory]`).

## Conventions

- **Aucun appel réseau externe en CI.** Wikidata, Wikipédia, pages vues, LLM, synthèse vocale et YouTube sont remplacés par des doublures déterministes (`FakeContentServices`, `FakeWikidata`, `FakePageviews`…). Le fournisseur `Factory:Llm:Provider=disabled` existe aussi pour les environnements sans modèle.
- Les tests d'intégration prennent une **base neuve par test** dans le serveur partagé (`PostgresFixture.CreateDatabaseAsync`) et y appliquent les vraies migrations.
- Le temps est injecté (`TimeProvider`) ; le moteur de déclenchement n'a pas d'horloge système.
- Les noms de tests décrivent le comportement ; les erreurs métier attendues sont testées via leur code, les exceptions réservées à l'inattendu.

## Rejeu GPS et résultats approuvés

`data-pipeline/gpx/` contient six traces **synthétiques** (marche au Vieux-Port, vélo sur la Corniche, voiture Route des Crêtes, autoroute à 110 km/h, perte GPS en tunnel, GPS imprécis en canyon urbain), produites par `generate_synthetic.py`. `tests/OnVoyage.App.Core.Tests/Discovery` les rejoue sur une horloge virtuelle et compare les déclenchements (lieu, horodatage, état) aux fichiers `Approved/*.approved.json`. Pour régénérer après un changement voulu du moteur :

```bash
UPDATE_APPROVED=1 dotnet test --project tests/OnVoyage.App.Core.Tests -c Release
```

puis relire le diff des fichiers approuvés avant de les commiter. Les traces réelles de la tâche H-001 ne sont pas encore là : `min_trigger_score` (0,45) reste à recalibrer dessus (ADR-0010).

**Corpus de traces (H-001).** `GpxCorpusTests` rejoue **tous** les `data-pipeline/gpx/*.gpx` contre leur `<trace>.expected-places.json` (format : `expected-places.schema.json`, modèle : `expected-places.template.json`) : lieux à déclencher, lieux interdits, bornes, mode, fenêtre horaire ; `status: draft` ne vérifie que la structure, `approved` rejoue le moteur. Une trace est **synthétique** quand l'attribut `creator` de son GPX le dit ; toute autre trace sans fichier de lieux attendus fait échouer le test. Protocole d'enregistrement, validation et nettoyage (`tools/gpx-check`) : [field/h001-traces-gpx.md](field/h001-traces-gpx.md). Tests des outils Python : `python3 -m unittest discover -s tools/gpx-check` et `-s tools/import-founder-creators` ; script de secrets : `bash deploy/check-secrets.test.sh` (tâche CI `tools` et `deploy-config`).

## Intégration continue

`.github/workflows/ci.yml`, à chaque PR et à chaque fusion sur `main` : restauration, compilation (avertissements en erreurs), `dotnet format --verify-no-changes`, **tests d'architecture** (étape à part, pour échouer vite), puis tous les tests, audit des paquets vulnérables (échec sur gravité haute ou critique), publication de la PWA. Une seconde tâche valide les fichiers de déploiement (`docker compose config`, shellcheck). `osm2pgsql` est installé sur le runner pour les tests de Factory.

`.github/workflows/backup-restore-test.yml` : test de restauration des sauvegardes (chiffrement, restauration dans une base vierge, comparaison des comptes de lignes de chaque table, exercice de restauration à un instant donné), chaque mois et à chaque changement des scripts. Voir [runbooks/restore.md](runbooks/restore.md).

## Tests manuels

Le §20.5 prévoit une recette terrain (Vieux-Port → Fort Saint-Jean → Panier à pied, Corniche à vélo, Route des Crêtes, iPhone et Android, batterie mesurée) dans `docs/testing/manual/field-test.md`. **Ce dossier n'existe pas encore** : l'essai sur appareil (audio écran verrouillé, GPS réel) est listé dans « Reste à faire » de [MOBILE.md](MOBILE.md). Toute exigence qui ne peut pas être couverte automatiquement doit recevoir sa procédure dans `docs/testing/manual/` (définition de « terminé », §0.3).

Tests de déploiement : `deploy/staging/smoke-test.sh <url>` (santé du Gateway, route publique vers Platform, session anonyme puis appel authentifié à Catalog, PWA, back-office) ; `deploy/staging/backup/test-restore.sh` et `test-pitr.sh` (sauvegarde et restauration).
