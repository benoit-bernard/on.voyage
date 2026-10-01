# ADR-0003 — Catalog sur PostgreSQL/PostGIS (T-101, T-102)

- Schéma `catalog` (EF Core 10 + Npgsql + NetTopologySuite, noms en `snake_case`) : `destination`, `taxonomy_node`, `poi`, `poi_text`, `poi_interest`, `poi_ethics`, `story` (§11.1). Index : GiST sur `poi.location` (geography 4326), GIN `tsvector` français et trigramme sur `poi_text`, unicité `(destination, slug)`, index partiel des histoires publiées.
- La proximité est poussée en base : `ST_DWithin` pour le rayon et `ST_Distance` (mètres) pour le tri. La position n'est jamais stockée.
- Le texte d'une histoire Premium ne peut pas être stocké dans le catalogue public (contrainte `ck_story_premium_text`, F-16).
- Les migrations s'appliquent au démarrage (`Catalog:Migrate`, défaut `true`). Le jeu de démonstration Marseille n'est chargé que si `Catalog:SeedDemoData=true` et le catalogue est vide ; la taxonomie v1 (74 nœuds) est toujours semée.
- Écarts : pas encore de `interest_vector vector(D)` (pgvector, utile au service Discovery), ni `story_audio_part`, `story_source`, `media_image`, `external_link`, `pack`. `poi_ethics` ne porte que `crowd_profile`, `fragile`, `access_regulated`. `GET /pois/{slug}` cherche le slug dans toutes les destinations (une seule au MVP-0).
- Wolverine : l'intégration `WolverineFx.EntityFrameworkCore` est en place (ADR-0004). Les ports sont implémentés au-dessus de `DbContext` enregistrés par fabrique, ce que Wolverine ne peut atteindre que par résolution de service : `ServiceLocationPolicy.AllowedButWarn` est activé dans `OnVoyage.Messaging` (les handlers restent sans type d'infrastructure).
- Tests : Testcontainers (`postgis/postgis:16-3.4`) ; sans Docker, `ONVOYAGE_TEST_PG` désigne un serveur PostGIS existant.
