# Bibliothèques JavaScript embarquées (carte, T-605)

Servies par l'application elle-même : aucun CDN, aucune requête vers un tiers (§14.4, vie privée).

| Dossier | Paquet npm | Version | Licence |
| --- | --- | --- | --- |
| `maplibre-gl/` | `maplibre-gl` | 6.11.2 | BSD-3-Clause |
| `pmtiles/pmtiles.mjs` | `pmtiles` (build ESM) | 4.5.0 | BSD-3-Clause |
| `pmtiles/fflate.mjs` | `fflate` (build navigateur ESM), import relatif réécrit dans `pmtiles.mjs` | 0.8.3 | MIT |
| `basemaps/basemaps.mjs` | `@protomaps/basemaps` (build ESM) | 5.7.2 | BSD-3-Clause |

Mise à jour : `npm i <paquet>@<version>` dans un dossier temporaire, copier les fichiers ci-dessus, retirer les lignes `sourceMappingURL`, puis rejouer le test de fumée de la carte (page avec `js/map.js`, aucun appel externe attendu).

## Polices (glyphes)

Le style attend `fonts/{fontstack}/{range}.pbf` sous `_content/OnVoyage.UI.Components/` (piles `Noto Sans Regular`, `Noto Sans Medium`, `Noto Sans Italic`). Ces fichiers sont ceux du dépôt `protomaps/basemaps-assets` (licence OFL) : ils n'étaient pas téléchargeables depuis l'environnement de développement, il faut donc les ajouter dans `wwwroot/fonts/` avant la mise en production. Sans eux, la carte s'affiche mais les libellés de rues et les nombres des grappes sont absents.
