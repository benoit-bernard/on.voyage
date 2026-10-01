# ADR-0014 — Site web public (T-901, T-902, T-903)

- Statut : accepté. Hors périmètre : pages `/@handle` et bloc « Vu par les créateurs » (T-1204), liens universels réels (identifiants des comptes développeur, H-003).

## Choix
- `OnVoyage.Web.Public` : ASP.NET Core, composants Razor en **rendu statique côté serveur** (aucune interactivité, aucun WebAssembly). Les composants ne sont pas ceux de la RCL de l'app : les pages du site sont des documents, pas des écrans ; ce choix s'écarte de la phrase « mêmes composants » du §F-24.
- Données : le Catalog, appelé directement avec un jeton `internal` de 5 minutes (`InternalTokens`), mis en cache 5 minutes. Seul le **texte** des histoires s'affiche, jamais l'audio ; une histoire sans texte (Premium) fait que la page n'existe pas (404). Une page n'existe que dans la langue d'une histoire publiée ; `hreflang` et sitemap suivent.
- Pages : `/`, `/{fr|en}/{destination}`, `/{lang}/{destination}/{lieu}`, pages légales (`mentions`, `conditions` avec l'ancre `#fouille-de-textes`, `confidentialite`, `sources`), `/micro-aventures/**`. JSON-LD `TouristDestination` / `TouristAttraction` / `Article` / `WebSite`, URL canoniques, Open Graph, `sitemap.xml` avec alternates.
- **Robots (T-902)** : `robots.txt` généré à partir de la même liste que le Gateway (`security.blocked_user_agents`, valeur livrée avec Platform, surchargeable par `Security:BlockedUserAgents`) ; middleware 403 par sous-chaîne insensible à la casse sur toutes les routes sauf `/health` ; en-têtes `tdm-reservation` / `tdm-policy` sur les réponses HTML, balises `<meta>` et `/.well-known/tdmrep.json` (annexe B). Pas de `nosnippet` (Q-07).
- **Blog (T-903)** : l'ancien site statique (branche `main`) est importé par `tools/import-legacy-blog.py` en fichiers `Content/micro-aventures/*.json` (6 articles). Les images d'en-tête, hébergées chez YouTube, sont retirées (aucune requête tierce) ; Tailwind et les polices distantes sont remplacés par une feuille de style locale. Les 9 anciennes adresses `.html` répondent **301** vers `/micro-aventures/...` (articles, `index` → liste, `sur-leau` → `/micro-aventures/eau`, `sur-terre` → `/micro-aventures/terre`), toutes testées. Le worker Instagram de l'ancien dépôt n'est pas repris.
- Liens universels : `/.well-known/apple-app-site-association` et `assetlinks.json` avec des identifiants de substitution à remplacer (`Public:AppleAppId`, `AndroidPackage`, `AndroidCertSha256`).

## Non vérifié
Rendu visuel dans un navigateur, temps de rendu P95 < 300 ms (cache chaud seulement), validation des données structurées par les outils des moteurs, textes juridiques (placeholders signalés dans les pages).
