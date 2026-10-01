# Q-2026-10-01 — Sources de la Factory : points à confirmer

1. **YAML ou JSON (§0.4, dépendance hors §10)** — `data-pipeline/taxonomy/mappings.yml` et `data-pipeline/osm/tags.yml` sont écrits en JSON (`mappings.json`) et directement dans le style Lua. Lire du YAML en C# demande YamlDotNet, absent de la liste. *Proposition* : garder JSON ; ou autoriser YamlDotNet.
2. **Identifiants Wikidata** — à vérifier avant le premier import : `Q9259` (patrimoine mondial), `Q10387689` (classé monument historique), `Q10387575` (inscrit), et les classes `wd:Q…` de `mappings.json`. Ils sont configurables (`Factory:Heritage`, `Factory:ClassificationRulesPath`).
3. **Licence ODbL (Q-08)** — la base `factory.place` est une base dérivée d'OSM ; le partage à l'identique s'applique si elle est rendue publique. Le catalogue public n'expose que des lieux choisis ; à faire valider.
4. **Géofabrik** — l'extrait PACA fait environ 200 Mo : le job le télécharge au plus une fois par jour (`Factory:Osm:ReuseDownloadDays`). Confirmer que le VPS de staging a la place et la bande passante.
5. **Accès réseau du worker** — domaines à ouvrir en sortie : `download.geofabrik.de`, `query.wikidata.org`, `wikimedia.org`, `*.wikipedia.org`, puis `api.openai.com` pour la suite.
