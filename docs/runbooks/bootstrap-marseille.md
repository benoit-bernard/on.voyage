# Runbook — amorcer le catalogue de Marseille

Objectif : ouvrir la destination `marseille` de bout à bout (lieux OSM, scores, sources Wikipédia, faits, histoires, voix, publication dans le Catalog), **avec un plafond de coût**, de façon reprenable et sans jamais régénérer ce qui existe. Décisions : [ADR-0017](../adr/0017-snapshot-et-amorcage-d-une-destination.md).

> État de vérification : le chemin en ligne n'a **jamais tourné contre les vrais services** (OpenAI, Wikipédia, Wikidata, Geofabrik) ; il est testé avec de faux clients. Le premier amorçage réel doit se faire avec un petit plafond (voir « Premier essai »).

## 0. Deux façons d'avoir du contenu
| Besoin | Commande |
| --- | --- |
| Démo immédiate, sans clé ni réseau : 36 lieux et histoires écrits à la main (brouillons IA à relire), audio `espeak-ng` | `scripts/dev-local.sh up` (le worker importe `data-pipeline/marseille/` au démarrage) ou `… -- snapshot marseille` |
| Catalogue généré par le pipeline avec les vrais fournisseurs | ce runbook |

Les deux se complètent : le snapshot donne un catalogue immédiatement utilisable ; l'amorçage réel ajoute les lieux OSM. Voir l'avertissement sur les doublons à la fin.

## 1. Prérequis
- PostgreSQL + PostGIS migré (les services Factory migrent au démarrage, `Factory:Migrate`).
- Binaires dans le `PATH` du worker : `osm2pgsql` (import OSM), `ffmpeg` (voix). En local : `apt install osm2pgsql ffmpeg`.
- Réseau sortant : `download.geofabrik.de`, `query.wikidata.org`, `*.wikipedia.org`, `wikimedia.org`, `api.openai.com` (voir Q-2026-10-01-factory-sources).
- Le Catalog doit tourner (il consomme les événements de publication) et servir le même dossier média que le worker (`Media:RootPath` = `Factory:MediaDirectory`).

## 2. Configuration (variables d'environnement du worker **et** de factory-api)
```
Factory__Llm__Provider=openai
OpenAI__ApiKey=<clé de projet>                       # secret, jamais dans le dépôt
Factory__Llm__ExtractorModel=<modèle>                # les quatre sont exigés au démarrage
Factory__Llm__WriterModel=<modèle>
Factory__Llm__VerifierModel=<modèle>
Factory__Llm__ClassifierModel=<modèle>
Factory__Tts__Model=gpt-4o-mini-tts                  # défaut
# Tarifs en USD (sans eux l'amorçage refuse de démarrer : un plafond sans prix ne plafonne rien)
Factory__Llm__Pricing__<modèle>__InputPerMillion=<prix>
Factory__Llm__Pricing__<modèle>__OutputPerMillion=<prix>
Factory__Llm__Pricing__gpt-4o-mini-tts__PerMillionCharacters=<prix>
Factory__MediaDirectory=/chemin/media                # = Media__RootPath du Catalog
```
Éviter les points dans les noms de modèles (clé hiérarchique). Les noms et prix sont à renseigner (Q-2026-10-01-openai).

## 3. Lancer
Ligne de commande (rapport JSON, code de sortie 0 si `completed`) :
```
dotnet run --project src/Services/Factory/OnVoyage.Factory.Worker -- bootstrap marseille \
  --Bootstrap:MaxPlaces=40 --Bootstrap:BudgetUsd=5 --Bootstrap:MinImportance=40 \
  --Bootstrap:AutoPublish=true
```
ou par l'API d'administration (jeton `admin`), exécuté en arrière-plan par le worker :
```
POST /api/factory/v1/admin/bootstrap
{ "destination": "marseille", "maxPlaces": 40, "budgetUsd": 5, "minImportance": 40, "autoPublish": true }
```
(202, puis suivre le journal du worker ; le rapport final y figure.)

| Option | Défaut | Effet |
| --- | --- | --- |
| `MaxPlaces` | 40 (1 à 500) | nombre de lieux traités, les plus importants d'abord |
| `MinImportance` | aucun | importance minimale (0 à 100) |
| `Lang` | `fr` | langue des histoires |
| `BudgetUsd` | 5 | **plafond de coût** de cette exécution (somme de `factory.llm_call.cost_usd`) |
| `AutoPublish` | `false` | approuve les histoires `Checked`, les voise et les publie. Sans cela, le travail s'arrête aux brouillons contrôlés, à relire dans le back-office |
| `ForceImport` | `false` | refait l'import OSM / enrichissement / scoring même si des lieux existent |
| `SkipImport` | `false` | ne touche pas aux lieux (travaille sur ce qui existe) |
| `AllowUnpriced` | `false` | autorise l'exécution sans tarifs (aucun plafond réel) |
| `PauseMilliseconds` | 500 | pause entre deux lieux |
| `RetryDelaysSeconds` | 10, 60, 300 | reprises sur panne du fournisseur |

## 4. Ce que fait chaque étape (et ce qui est mis en cache)
| Étape | Cache / idempotence |
| --- | --- |
| Import OSM → `factory_raw.osm_place_*` → `factory.place` | sauté s'il y a déjà des lieux (sauf `ForceImport`) ; l'extrait Geofabrik téléchargé est réutilisé 1 jour (`Factory:Osm:ReuseDownloadDays`) |
| Enrichissement Wikidata + pages vues | seuls les lieux sans enrichissement sont interrogés |
| Scoring, dédoublonnage | recalcul sans appel payant ; décisions d'éditeur jamais écrasées |
| Sources Wikipédia | un texte inchangé (SHA-256) n'est pas enregistré de nouveau |
| Faits (modèle) | un appel par document sans faits ; **un fait n'est gardé que si sa citation est mot pour mot dans le document** |
| Histoire (modèle + vérificateur) | sautée si le lieu a déjà une histoire vivante dans la langue |
| Voix | un morceau déjà enregistré pour (histoire, voix) n'est pas resynthétisé ; fichiers sous `audio/{lieu}/{langue}/{type}/v{version}_{voix}_{partie}.mp3` |
| Publication | événements `PoiPublishedV1`, `StoryPublishedV1` vers Catalog, Discovery |

**Régénération** : uniquement si les sources ou la version du prompt changent et qu'une personne le demande (nouvelle version d'histoire par le back-office, `POST /admin/places/{id}/stories`). L'amorçage ne réécrit jamais une histoire existante.

## 5. Arrêts et reprise
| `outcome` | Signification | Action |
| --- | --- | --- |
| `completed` | tous les lieux choisis traités | relire les `NeedsReview` dans le back-office |
| `budget_exhausted` | le plafond est atteint | relancer avec un plafond plus haut : la reprise saute ce qui est fait |
| `provider_unavailable` | trois lieux de suite en panne du fournisseur | attendre, relancer |
| erreur `prices_missing` | fournisseur payant sans tarif | configurer les prix |

Les lieux sans assez de faits (moins de `Factory:Content:MinFacts`, 3) sont listés dans les étapes `place:<slug>` du rapport avec leur raison : pas d'article Wikipédia, texte trop court… Une personne décide.

## 6. Premier essai recommandé
1. `--Bootstrap:MaxPlaces=3 --Bootstrap:BudgetUsd=0.5` sans `AutoPublish` : vérifier les tarifs, le coût réel (`select sum(cost_usd) from factory.llm_call`), la qualité des brouillons.
2. Écouter une histoire voisée (back-office → atelier) avant d'ouvrir plus grand.
3. Puis `MaxPlaces=40 BudgetUsd=<plafond choisi> AutoPublish=true`.

## 7. Après l'amorçage
- Les histoires publiées automatiquement sont des **brouillons IA** (`aiGenerated = true` dans le Catalog) : relecture éditoriale à planifier.
- Si le snapshot était déjà chargé : ouvrir les propositions de fusion (`GET /admin/dedup?destination=marseille`) ; les lieux du snapshot et ceux d'OSM de même nom à moins de 75 m sont fusionnés ou proposés. Le lieu conservé est celui qui a un identifiant Wikidata, sinon le plus ancien.
- Média : `Factory__MediaDirectory` doit être sauvegardé avec la base (l'audio est un cache coûteux à refaire).
