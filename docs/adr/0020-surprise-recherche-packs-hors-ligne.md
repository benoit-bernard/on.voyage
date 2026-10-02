# ADR-0020 — Surprenez-moi, recherche, packs hors ligne et moteur embarqué (T-615, T-616, T-307, T-617, T-620)

- Statut : accepté. Suite de l'ADR-0010 (§ « Données locales ») et de l'ADR-0019.

## Surprenez-moi (T-615)

- Client : `SurpriseService` (`OnVoyage.App.Core/Surprise`) appelle `GET /api/discovery/v1/surprise?lat&lng&radius` (nouvelle méthode `IDiscoveryClient.GetSurpriseAsync`, 404 `surprise_not_found` = plus rien à proposer). Le tirage et les exclusions (lieux fragiles, réglementés, très fréquentés, 20 derniers proposés) restent **côté serveur** (§6.10, déjà implémenté) ; le client n'a aucune règle de choix. La position n'est que dans les paramètres de cet appel (3 décimales, autorisé par la règle des endpoints `surprise`).
- La phrase d'explication est écrite par l'app à partir du modèle (`why.template`) et, pour une exploration, de la catégorie de la fiche ; la distance est calculée sur l'appareil à partir des coordonnées de la fiche (non conservées). Événement `surprise_requested` avec `results_count` seulement.
- Page `/surprise` (boutons sur l'accueil et la carte). Le critère « 20 appels, au moins 8 lieux distincts » est un test de l'API Discovery (serveur) ; côté app, le test vérifie que le client ne boucle pas.
