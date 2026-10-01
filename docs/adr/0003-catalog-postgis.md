# ADR-0003 — Catalog sur PostgreSQL/PostGIS (T-101, T-102)

- Schéma `catalog` (EF Core 10 + Npgsql + NetTopologySuite, noms en `snake_case`) : `destination`, `taxonomy_node`, `poi`, `poi_text`, `poi_interest`, `poi_ethics`, `story` (§11.1). Index : GiST sur `poi.location` (geography 4326), GIN `tsvector` français et trigramme sur `poi_text`, unicité `(destination, slug)`, index partiel des histoires publiées.
- La proximité est poussée en base : `ST_DWithin` pour le rayon et `ST_Distance` (mètres) pour le tri. La position n'est jamais stockée.
- Le texte d'une histoire Premium ne peut pas être stocké dans le catalogue public (contrainte `ck_story_premium_text`, F-16).
- Les migrations s'appliquent au démarrage (`Catalog:Migrate`, défaut `true`). Le jeu de démonstration Marseille n'est chargé que si `Catalog:SeedDemoData=true` et le catalogue est vide ; la taxonomie v1 (74 nœuds) est toujours semée.
- Écarts : pas encore de `interest_vector vector(D)` (pgvector, utile au service Discovery), ni `story_audio_part`, `story_source`, `media_image`, `external_link`, `pack`. `poi_ethics` ne porte que `crowd_profile`, `fragile`, `access_regulated`. `GET /pois/{slug}` cherche le slug dans toutes les destinations (une seule au MVP-0).
- Wolverine : le lecteur dépend d'un `DbContext` enregistré par fabrique ; `ServiceLocationPolicy.AllowedButWarn` est donc activé en attendant l'intégration `WolverineFx.EntityFrameworkCore` + boîte d'envoi PostgreSQL (T-006).
- Tests : Testcontainers (`postgis/postgis:16-3.4`) ; sans Docker, `ONVOYAGE_TEST_PG` désigne un serveur PostGIS existant.
