# Q-2026-10-02 — Extrait PMTiles de la carte dans les packs hors ligne

**Contexte (§14.6, T-307)** : un pack contient `map.pmtiles`, l'extrait vectoriel de l'emprise de la destination (zoom 0–15). L'extraction se fait avec l'outil en ligne de commande `pmtiles extract SOURCE SORTIE --bbox=… --maxzoom=15` (go-pmtiles). Le §10 ne liste que la bibliothèque JavaScript `pmtiles`, pas ce binaire.

**Ce qui est fait** : le port `IMapExtractor` et un adaptateur (`PmtilesCliMapExtractor`) qui appelle le binaire par une liste d'arguments (jamais une chaîne de shell) si `Factory:Pmtiles:Binary` et `Factory:Pmtiles:Source` sont renseignés. Sans eux, le pack est construit **sans** `map.pmtiles` et l'app garde la carte en ligne.

**À décider côté propriétaire produit**
- [ ] Autoriser l'ajout du binaire go-pmtiles (licence BSD-3) à l'image du worker Factory.
- [ ] Fournir la source PMTiles de la région (fichier planétaire ou régional servi avec requêtes Range) et mesurer la taille de l'extrait de Marseille (spike T-617 : l'estimation du §14.6 est « à mesurer »).
