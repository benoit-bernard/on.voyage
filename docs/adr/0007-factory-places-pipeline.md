# ADR-0007 — Factory : pipeline des lieux (T-201 à T-205) et publication vers Catalog (T-103)

- Statut : accepté. Couvre l'import OSM, l'enrichissement Wikidata/Wikipédia, le dédoublonnage, la classification, les scores et la publication des lieux. Les histoires (faits, rédaction, contrôles, voix) restent à faire (T-301 à T-308).

## Découpage
`Factory.Domain` (pur, sans fournisseur) : `DuplicateDetector` et `NameSimilarity` (trigrammes comme pg_trgm), `RuleClassifier` et `ClassificationDecision`, `ImportanceScorer`, `PopularityPercentiles`, `CrowdProfileBuilder`, `HiddenGemRule`. `Factory.Application` : `ImportPlaces → EnrichPlaces → ScorePlaces` (chaque étape renvoie la suivante, Wolverine la remet en file : reprise indépendante), `PublishPlace`, `UnpublishPlace`, décisions éditoriales et fusions. `Factory.Infrastructure` : EF Core (`factory`, `factory_raw`), `OsmImporter`, clients Wikimedia. `Factory.Api` : back-office (`/api/factory/v1/admin`, rôle admin) ; `Factory.Worker` : écoute la file `factory` et exécute les jobs (le long travail ne tourne jamais dans une requête).

## Choix
- **osm2pgsql** (flex, `data-pipeline/osm/onvoyage.lua`) lancé comme processus enfant, mot de passe par variable d'environnement, table datée `factory_raw.osm_place_YYYYMMDDHHmm`, emprise de la destination (`--bbox`), deux tables conservées. Le filtre des objets nommés suit §7.2 ; un extrait OSM de test (`Data/marseille-sample.osm`) fait tourner le vrai binaire en CI.
- **Provenance** (§7.8) : chaque lieu porte `source`, `source_license` (ODbL-1.0), `source_url`, `retrieved_at`, version OSM ; Wikidata (CC0) et pages vues sont dans `factory_raw`, séparés des scores.
- **Dédoublonnage** (§7.4) : même QID → fusion ; QID différents → toujours distincts ; sinon < 75 m et trigrammes ≥ 0,6 (auto à 0,85) ; un point dans l'emprise d'un autre lieu reste distinct sauf nom quasi identique. Fusions journalisées (`dedup_link`), réversibles ; une fusion annulée n'est jamais refaite par un passage suivant.
- **Classification** (§7.5) : règles `data-pipeline/taxonomy/mappings.json`, puis repli modèle (port `IPlaceModelClassifier`, absent pour l'instant → `NeedsReview`). Un lieu en révision ne peut pas être publié.
- **Scores** (§7.6) : importance, centile de popularité, profil d'affluence, pépite, tous paramétrés par `ScoringOptions` ; la surcharge éditoriale et la liste « saturée » priment. Un nouveau passage ne touche ni aux décisions d'un éditeur ni aux poids saisis à la main.
- **Publication** : `PoiPublishedV1` / `PoiUnpublishedV1` par l'outbox vers la file de Catalog, version croissante, consommation idempotente ; une publication peut créer la destination si elle manque, et un slug déjà pris reçoit un suffixe plutôt que de bloquer.
- **Jobs HTTP** : l'API dépose le job dans la file durable et répond 202.

## Écarts avec le cahier des charges
- Les règles de classification et la liste des tags sont en **JSON et Lua**, pas en YAML : lire du YAML demande une dépendance (YamlDotNet) hors de la liste du §10 (voir `docs/questions/Q-2026-10-01-factory-sources.md`).
- Le rattrapage Wikidata par coordonnées (§7.3 étape 3), l'extraction des documents sources Wikipédia (§7.3 étape 4) et le tag `heritage` des objets sans QID ne sont pas faits.
- Les identifiants Wikidata du patrimoine (`Q9259` UNESCO, `Q10387689` classé, `Q10387575` inscrit) et des classes de `mappings.json` sont **à vérifier sur wikidata.org** avant le premier import réel : aucun accès réseau n'était possible pour le faire.
- **Rien n'a pu être testé contre les services réels** (Geofabrik, Wikidata, Wikimedia, OpenAI sont bloqués depuis cet environnement) : les clients sont testés sur des réponses simulées, aux formes documentées.
- Pas d'interface d'administration Blazor (T-402) : seulement l'API.
