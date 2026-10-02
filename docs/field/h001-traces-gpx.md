# H-001 — Enregistrer 6 traces GPX réelles et leurs lieux attendus

Tâche humaine H-001 (cahier des charges §20.4, §24.2). Durée estimée : 4 à 6 h, sur une ou deux journées. Elle débloque la calibration du moteur de déclenchement (T-610) : `min_trigger_score` (0,45) est à recalibrer sur ces traces (§14.5, ADR-0010).

Ce que l'agent a préparé pour que ce soit rapide et sûr :

| Outil | Rôle |
| --- | --- |
| `tools/gpx-check/gpx_check.py` | vérifie une trace brute contre son scénario, la **nettoie** (métadonnées, 100 m à chaque bout, dates), liste les lieux passés à proximité, produit le fichier de lieux candidats |
| `data-pipeline/gpx/expected-places.schema.json` et `expected-places.template.json` | format du fichier de lieux attendus |
| `tests/OnVoyage.App.Core.Tests/Discovery/GpxCorpusTests.cs` | rejoue **chaque** `data-pipeline/gpx/*.gpx` dans le moteur et compare avec son `*.expected-places.json` |

Les six traces **synthétiques** actuelles (`walk_vieux_port`, `bike_corniche`, `car_route_des_cretes`, `car_a50_highway_110kmh`, `tunnel_loss`, `gps_jitter_urban_canyon`) restent en place : elles sont reconnaissables à `creator="ON.VOYAGE synthetic generator"`. Les traces réelles s'ajoutent à côté, avec le préfixe `real_`.

## Vue d'ensemble

1. Régler le téléphone et l'application d'enregistrement (section 2), faire un essai de 2 minutes.
2. Enregistrer les 6 parcours (section 3). Pendant chaque parcours, **noter les lieux réellement vus** (section 5).
3. `gpx_check.py check` puis `sanitize` (section 4) : le fichier propre va dans `data-pipeline/gpx/`.
4. Écrire `<trace>.expected-places.json` (section 5), en `draft`.
5. Lancer le test, relire, passer en `approved` (section 6).
6. Une PR par lot de traces. Ne pas commiter de trace brute.

Prérequis : `python3` (3.9 ou plus, rien à installer) ; pour le test, le SDK .NET du dépôt.

## 1. Les six scénarios

Les seuils sont ceux de `python3 tools/gpx-check/gpx_check.py scenarios`. Ils sont volontairement larges : l'outil détecte un mauvais enregistrement, pas un mauvais marcheur.

| Clé | Scénario (§20.4) | Parcours proposé à Marseille | Ce que la trace doit montrer |
| --- | --- | --- | --- |
| `walk` | Marche 3 km/h | Vieux-Port (Ombrière) → Hôtel de Ville → Fort Saint-Jean et Mucem → Le Panier → Vieille Charité (environ 3 km, 45 à 60 min, avec 2 ou 3 pauses de 30 à 60 s) | 15 à 120 min, ≥ 1 km, vitesse médiane 2 à 6,5 km/h, aucun trou |
| `bike` | Vélo 20 km/h | Corniche Kennedy, du Vallon des Auffes à la plage du Prophète, aller-retour si besoin d'atteindre 8 min | 8 à 90 min, ≥ 2 km, médiane 12 à 30 km/h, aucun trou |
| `car50` | Voiture 50 km/h | **En passager.** Corniche Kennedy puis route des Goudes (limitation en général à 50 km/h : suivre la signalisation) ; hors Marseille, la route des Crêtes (Cassis – La Ciotat) est le décor du §20.5 si une sortie est prévue | 5 à 90 min, ≥ 4 km, médiane 30 à 70 km/h |
| `highway110` | Autoroute 110 km/h | **En passager.** A50 entre Marseille et Aubagne sur les tronçons à 110 (si la limite est 90, c'est accepté : médiane ≥ 85 km/h) | 5 à 90 min, ≥ 8 km, médiane 85 à 130 km/h |
| `tunnel` | Perte GPS (tunnel) | **En passager.** Tunnel Prado-Carénage (péage, environ 2,5 km) ou tunnel de la Major ; le téléphone doit perdre le signal. Vérifier sur place le tunnel choisi | Un trou de **45 s à 15 min** dans la trace, le reste continu. Ne jamais éditer la trace à la main : l'outil le repère |
| `canyon` | GPS imprécis (canyon urbain) | À pied, rues étroites et hautes : Le Panier, rue Caisserie, rue de la République | ≥ 20 % de points imprécis (> 30 m) ou en saut ; marche à vitesse normale |

Le scénario « perte et retour réseau » du §20.4 n'est **pas** une trace GPX (c'est le réseau, pas le GPS) : sur la marche, activer le mode avion 5 minutes en route et noter l'heure dans le carnet. Il servira à la recette terrain (`docs/testing/manual/field-test.md`, §20.5) et ne produit pas de fichier.

Sécurité : en voiture, l'enregistrement est piloté **par un passager**, téléphone fixé ou en poche selon le scénario ; ne manipulez jamais le téléphone au volant. À vélo, démarrez l'enregistrement à l'arrêt.

## 2. Réglages du téléphone et de l'application

### Application d'enregistrement

Il faut une application qui enregistre **un point par seconde**, avec l'**horodatage** de chaque point et, idéalement, la **précision** (`accuracy` ou `hdop`), et qui exporte du GPX. Applications libres et sans compte, à privilégier :

- Android : *GPSLogger for Android* (open source), *OpenTracks* (open source), ou *OsmAnd* (enregistrement de trace).
- iOS : *GPX Tracker* (open source) ou *OsmAnd*.

À éviter : les applications qui exigent un compte (Strava, Komoot…) — le GPX exporté embarque votre profil, le nom de l'activité, parfois l'appareil. Si vous n'avez que celles-là, `sanitize` retire tout cela, mais préférez ne pas y passer.

Dans l'application : intervalle **1 s**, distance minimale **0 m**, pas de filtre de précision (les mauvais points sont justement ce que le test veut voir), pas de « pause automatique », pas de lissage, format GPX 1.1.

### Android

- Localisation : « Précision élevée » (GPS + réseau), précision améliorée activée.
- Batterie de l'application d'enregistrement : « Sans restriction » ; désactiver l'économiseur de batterie pour ce parcours.
- Autoriser la localisation « Tout le temps » pour l'application.
- Écran verrouillé et téléphone en poche : c'est la condition réelle d'usage ; ne gardez l'écran allumé que pour la marche si l'application s'arrête sinon.

### iPhone

- Réglages › Confidentialité › Service de localisation : activé, l'application sur « Toujours », **Position précise** activée.
- Actualisation en arrière-plan activée pour l'application ; mode économie d'énergie désactivé.

### Pour tous

- Mode avion désactivé (sauf le test réseau de la marche), Wi-Fi activé ou non, peu importe.
- Batterie ≥ 60 % ; deux enregistrements simultanés ne sont pas nécessaires.
- Essai de 2 minutes dehors, ciel dégagé, avant chaque parcours : le fichier doit contenir des points toutes les secondes. Attendre 30 s à l'arrêt après avoir lancé l'enregistrement, le temps que le GPS s'accroche.
- Téléphone **porté comme le voyageur le portera** : en poche pour la marche et le vélo ; sur le tableau de bord ou en main du passager pour la voiture (au tunnel, en poche, la perte est plus nette).

## 3. Nommage et export

Noms de fichiers (minuscules, chiffres et `_`), déjà reconnus par les tests :

| Scénario | Fichier propre à commiter |
| --- | --- |
| walk | `data-pipeline/gpx/real_walk_vieux_port.gpx` |
| bike | `data-pipeline/gpx/real_bike_corniche.gpx` |
| car50 | `data-pipeline/gpx/real_car50_corniche_goudes.gpx` (ou `real_car50_route_des_cretes.gpx`) |
| highway110 | `data-pipeline/gpx/real_highway110_a50.gpx` |
| tunnel | `data-pipeline/gpx/real_tunnel_prado_carenage.gpx` |
| canyon | `data-pipeline/gpx/real_canyon_panier.gpx` |

Le nom du fichier sans extension est le champ `trace` du fichier de lieux attendus. Exportez le GPX brut dans un dossier **hors du dépôt** (par exemple `~/gpx-bruts/`) : seules les traces nettoyées entrent dans le dépôt.

## 4. Valider et nettoyer

Depuis la racine du dépôt :

```bash
# 1. Lister les seuils
python3 tools/gpx-check/gpx_check.py scenarios

# 2. Vérifier la trace brute. --home = coordonnées de votre domicile ou hôtel : sert seulement à comparer, jamais écrit.
python3 tools/gpx-check/gpx_check.py check ~/gpx-bruts/marche.gpx --scenario walk --home 43.2xxxx,5.3xxxx

# 3. Nettoyer (retire métadonnées, 100 m à chaque bout, décale les dates) et re-contrôler le résultat
python3 tools/gpx-check/gpx_check.py sanitize ~/gpx-bruts/marche.gpx --scenario walk --home 43.2xxxx,5.3xxxx \
  -o data-pipeline/gpx/real_walk_vieux_port.gpx
```

Ce que l'outil contrôle (code de sortie 1 s'il y a une erreur) :

- **Format** : GPX lisible, chaque point a `lat`, `lon`, `time` ; horodatages strictement croissants.
- **Durée, distance, nombre de points** propres au scénario.
- **Profil de vitesse** : médiane en mouvement (fenêtres de 10 s, robuste au bruit) et 95e centile ; détecte un mauvais mode de déplacement.
- **Cadence** : intervalle médian ≤ 5 s à pied et à vélo (6 s en canyon), 4 s en voiture, 3 s sur autoroute ; il faut donc régler 1 point par seconde.
- **Trous** : aucun trou de plus de 20 s (seuil du moteur) sauf pour `tunnel`, qui en exige un de 45 s à 15 min.
- **Bruit (jitter)** : au plus 10 % de points imprécis (> 30 m) ou en saut, sauf `canyon` qui en exige au moins 20 %.
- **Confidentialité** : aucun `<name>`, `<desc>`, `<author>`, `<email>`, `<link>`, `<keywords>`, `<wpt>`, `<rte>` renseigné ; aucune adresse e-mail, UUID, numéro de série ou IMEI dans le fichier ; extensions d'appareil signalées ; `creator` ne doit pas contenir d'identifiant.
- **Coupe de 100 m** : `sanitize` retire les 100 premiers et 100 derniers mètres **parcourus** ; avec `--home`, `check` vérifie que ni le début ni la fin n'est à moins de 100 m de ce point. Les dates sont décalées au 14 juin 2026 (`--keep-dates` pour les garder).

Un fichier qui n'est pas passé par `sanitize` est signalé (avertissement, erreur avec `--strict`). `sanitize` re-contrôle son résultat ; ajoutez `--strict` pour que les avertissements (par exemple « aucune précision dans la trace ») bloquent aussi.

Conseils quand `check` échoue : trous → économiseur de batterie ; médiane trop basse ou trop haute → mauvais scénario ou arrêts très longs ; trop de bruit hors `canyon` → refaire en ciel dégagé ; pas assez de bruit pour `canyon` → rues plus étroites et plus hautes, ou téléphone en poche.

### Vie privée

Le dépôt peut devenir public : une trace révèle où vous êtes passé et quand. Vérifiez sur une carte que la trace propre ne contient ni votre domicile, ni votre lieu de travail, ni l'adresse d'un tiers. Démarrez l'enregistrement sur un point public (Vieux-Port, parking) plutôt que devant chez vous ; la coupe de 100 m est un filet de sécurité, pas une garantie. Ne commitez jamais le GPX brut ni le carnet de notes.

## 5. Lieux attendus de chaque trace

Les lieux attendus sont **ce que vous avez réellement vu ou longé**, relevé pendant le parcours : ils ne sont pas la sortie du moteur. Sinon le test ne vérifie rien.

1. **Pendant le parcours** : noter dans un carnet (ou un mémo vocal, effacé ensuite) l'heure, le lieu vu et s'il était visible, accessible, derrière vous. Les lieux importants que vous n'avez *pas* vus (derrière, fermé, trop loin) comptent aussi : ils vont dans `mustNotTrigger`.
2. **Après le nettoyage**, générer la liste de lieux candidats et voir ce que la trace a frôlé :

```bash
# Lieux du snapshot Marseille (36 lieux) au format de rejeu. Les champs baseScore, visibleFromRoad et carAccessible sont des valeurs
# par défaut : éditer le fichier pour les lieux visibles de la route (voiture).
python3 tools/gpx-check/gpx_check.py candidates -o data-pipeline/gpx/real_walk_vieux_port.candidates.json

# Lieux passés à moins de 150 m, dans l'ordre du temps, avec la distance minimale
python3 tools/gpx-check/gpx_check.py near data-pipeline/gpx/real_walk_vieux_port.gpx \
  --candidates data-pipeline/gpx/real_walk_vieux_port.candidates.json --radius 150
```

   Le fichier `candidates.json` synthétique (19 lieux de test) reste le défaut ; pour une trace réelle utilisez le fichier du snapshot, ou ajoutez-y un lieu à la main (nom, coordonnées, `importance`, etc.). L'identifiant d'un lieu ne sert qu'au rejeu.
3. **Écrire le fichier** `data-pipeline/gpx/<trace>.expected-places.json` à partir de `expected-places.template.json`. Les noms de lieux sont exactement ceux du fichier de candidats (le test signale une faute de frappe).

```json
{
  "$schema": "./expected-places.schema.json",
  "schemaVersion": 1,
  "trace": "real_walk_vieux_port",
  "scenario": "walk",
  "status": "draft",
  "synthetic": false,
  "candidates": "real_walk_vieux_port.candidates.json",
  "expectedMode": "Walk",
  "minTriggers": 3,
  "mustTrigger": [ { "place": "MuCEM", "reason": "longé à 40 m, visible du chemin", "afterSeconds": 600, "beforeSeconds": 1500 } ],
  "mustNotTrigger": [ { "place": "Notre-Dame de la Garde", "reason": "hors de vue, trop loin" } ]
}
```

| Champ | Sens |
| --- | --- |
| `status` | `draft` : seule la structure est vérifiée ; `approved` : le rejeu doit retrouver les attentes |
| `synthetic` | `false` pour un enregistrement ; doit correspondre à l'attribut `creator` du GPX |
| `expectedMode` | `Walk`, `Bike` ou `Car` : tout déclenchement doit avoir ce mode (omettre pour `bike`, la trace commence souvent à l'arrêt) |
| `minTriggers`, `maxTriggers` | bornes du nombre d'histoires racontées ; en voiture, utiliser surtout `mustTrigger`/`mustNotTrigger` |
| `mustTrigger[].place`, `reason` | lieu vu ; raison obligatoire (« ce que j'ai vu »), jamais de donnée personnelle |
| `afterSeconds`, `beforeSeconds` | fenêtre en secondes depuis le début de la trace **nettoyée** (`near` donne ces temps) |
| `mustNotTrigger` | lieux qui ne doivent pas déclencher : derrière, fragile, peu important, dans un trou de signal |
| `candidates` | fichier des lieux (même dossier), `candidates.json` par défaut |

Scénarios particuliers : `tunnel` — le lieu situé dans le trou du signal est dans `mustNotTrigger` (aucune position extrapolée) et `maxTriggers` peut valoir 0 ; `canyon` — peu de déclenchements, tous sur des positions précises (`maxTriggers`) ; `car50` et `highway110` — un lieu devant et visible dans `mustTrigger`, un lieu derrière dans `mustNotTrigger`.

## 6. Rejeu, calibration, PR

```bash
dotnet build tests/OnVoyage.App.Core.Tests -c Release -m:1
tests/OnVoyage.App.Core.Tests/bin/Release/net10.0/OnVoyage.App.Core.Tests      # ou : dotnet test --project tests/OnVoyage.App.Core.Tests -c Release
```

Le test `GpxCorpusTests` parcourt tous les `.gpx` du dossier :

- une trace illisible ou désordonnée échoue ;
- une **trace réelle sans fichier de lieux attendus échoue** (message avec le nom attendu) ;
- un fichier `*.expected-places.json` sans GPX, avec une faute de nom de lieu, un scénario inconnu ou une contradiction échoue ;
- les fichiers `approved` sont rejoués dans le vrai `TriggerEngine` (horloge virtuelle) et comparés : lieu manquant, lieu interdit déclenché, mauvais horaire, trop ou pas assez d'histoires, mauvais mode.

Si un fichier `approved` échoue, **ne pas modifier les attentes pour faire passer le test** : c'est la calibration. Deux issues légitimes : régler le moteur (`min_trigger_score`, rayons ; réponse dans `docs/questions/Q-<date>-<sujet>.md` ou ADR, puis relecture des fichiers `Approved/*.approved.json` avec `UPDATE_APPROVED=1`, `docs/TESTING.md`), ou corriger vos notes si vous aviez mal relevé un lieu. Les fichiers `Approved/` des traces synthétiques ne changent pas.

Définition de « terminé » pour H-001 : 6 fichiers `real_*.gpx` propres (un par scénario), 6 `*.expected-places.json` en `approved`, `candidates` des traces, test vert, aucune trace brute dans le dépôt. Mettre ensuite à jour `docs/TESTING.md` (« traces réelles ») et lancer la calibration de T-610.
