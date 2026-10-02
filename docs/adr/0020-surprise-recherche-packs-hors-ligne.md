# ADR-0020 — Surprenez-moi, recherche, packs hors ligne et moteur embarqué (T-615, T-616, T-307, T-617, T-620)

- Statut : accepté. Suite de l'ADR-0010 (§ « Données locales ») et de l'ADR-0019.

## Surprenez-moi (T-615)

- Client : `SurpriseService` (`OnVoyage.App.Core/Surprise`) appelle `GET /api/discovery/v1/surprise?lat&lng&radius` (nouvelle méthode `IDiscoveryClient.GetSurpriseAsync`, 404 `surprise_not_found` = plus rien à proposer). Le tirage et les exclusions (lieux fragiles, réglementés, très fréquentés, 20 derniers proposés) restent **côté serveur** (§6.10, déjà implémenté) ; le client n'a aucune règle de choix. La position n'est que dans les paramètres de cet appel (3 décimales, autorisé par la règle des endpoints `surprise`).
- La phrase d'explication est écrite par l'app à partir du modèle (`why.template`) et, pour une exploration, de la catégorie de la fiche ; la distance est calculée sur l'appareil à partir des coordonnées de la fiche (non conservées). Événement `surprise_requested` avec `results_count` seulement.
- Page `/surprise` (boutons sur l'accueil et la carte). Le critère « 20 appels, au moins 8 lieux distincts » est un test de l'API Discovery (serveur) ; côté app, le test vérifie que le client ne boucle pas.

## Recherche (T-616)

- **Catalog** : `GET /api/catalog/v1/search?q&destination&limit` (absente jusqu'ici ; le schéma avait déjà la colonne `tsvector` française et l'index trigramme sur le nom). Requête LINQ unique (`PoiSearch`), traduite par EF Core en paramètres : `unaccent(nom) ILIKE` (préfixe puis sous-chaîne), `word_similarity` (≥ 0,4, tolère une faute) et `search_vector @@ plainto_tsquery('french')` (description, mots-clés). Classement : nom commençant par le texte, nom contenant le texte, similarité, bonus texte intégral. Les caractères `%`, `_` et `\` saisis sont échappés. Le texte saisi n'est jamais journalisé (même expurgation que la position).
- **Limites assumées** : pas d'alias (aucune colonne) ni de catégories dans l'index ; `unaccent(nom)` n'utilise pas l'index trigramme existant (calculé sur le nom brut) : parcours séquentiel d'une destination (milliers de lignes), acceptable au volume cible ; un index d'expression `gin (immutable_unaccent(name) gin_trgm_ops)` demandera une migration si besoin. **Les tests d'intégration (`CatalogApiTests` : « cathedrale » dans les 3 premiers, mot-clé, injection, caractère générique) compilent mais n'ont pas pu être exécutés ici** (pas de Docker) ; la traduction SQL est vérifiée par un test unitaire sur `ToQueryString()`.
- **App** : `SearchSession` (debounce 300 ms, minimum 2 caractères, la dernière frappe seule compte, repli sur le pack si le réseau échoue via le port `IOfflineSearch`, branché sur le FTS5 de `PackReader` par T-617). Page `/recherche`. Événement `search_performed` avec le nombre de résultats seulement.
