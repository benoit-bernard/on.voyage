# ADR-0019 — Back-office : suivi des lots et de l'amorçage, signalements, vidéos, indicateurs

- Statut : accepté. Complète les ADR-0009 (back-office), 0012 (Insights) et 0017 (amorçage). Tâches T-305, T-404, T-405, T-406, T-407.

## 1. Suivi des lots et de l'amorçage (T-305, T-404)

**Ce qui existait** : lots (`generation_batch`) et tâches (`generation_job`), trois essais par tâche avec reprises espacées, lettres mortes de Wolverine, compteurs « terminés · échecs · à relire », relance des échecs, page `/admin/batches` avec rafraîchissement toutes les 5 s. L'amorçage (ADR-0017) ne laissait aucune trace hors du journal du worker.

**Ce qui est ajouté**
- **Annulation** : `POST /admin/batches/{id}/cancel` passe les tâches en attente à `Cancelled` ; une tâche en cours finit son essai (on ne coupe pas un appel payant au milieu). Un message livré pour une tâche annulée est ignoré par le gestionnaire.
- **Plafond de coût d'un lot** (`budgetUsd`, facultatif, dans les critères du lot) : avant chaque tâche, le coût déjà dépensé est comparé au plafond ; au-delà, la tâche passe à `Cancelled` avec la raison `budget_exhausted`. Le coût d'un lot n'est pas une colonne : c'est la somme de `factory.llm_call.cost_usd` entre la création du lot et la fin de sa dernière tâche. **Limite assumée** : `llm_call` ne porte pas l'identifiant du lot, donc deux lots qui tournent en même temps se comptent l'un l'autre (le plafond est alors prudent, jamais trop large).
- **Relance** : d'un lot (échecs et annulations) ou d'une tâche (`POST /admin/batch-jobs/{id}/retry`).
- **Vue des lettres mortes** (`GET /admin/dead-letters`) : lecture de `factory.wolverine_dead_letters` (type du message, date, exception) et, pour une tâche de lot, lien vers son lot et son lieu. Wolverine garde le corps du message en binaire : la tâche est retrouvée par le message de l'exception (`Job {id} failed n times.`), pas par le corps.
- **Amorçage suivi** : table `factory.bootstrap_run` (`Queued → Running → Completed | Stopped | Failed`, compteurs en direct, coût et plafond, étapes). `POST /admin/bootstrap` enregistre l'exécution **avant** de la confier au worker et renvoie son identifiant ; le worker met les compteurs à jour après chaque lieu ; la ligne de commande enregistre aussi ses exécutions. `POST /admin/bootstrap-runs/{id}/cancel` demande l'arrêt avant le prochain lieu (`outcome = cancelled`). Les écritures de suivi sont des `ExecuteUpdate` isolés : la progression est visible pendant que le gestionnaire (long) tient encore sa propre unité de travail.
- **Pages** : `/admin/batches` (filtres d'état et de destination, plafond, coût dépensé sur plafond, barre d'avancement), `/admin/batches/{id}` (filtre par état de tâche, relance d'une tâche, annulation, coût), `/admin/bootstrap` (formulaire, exécutions, arrêt, étapes), `/admin/dead-letters`. Le suivi en direct est une **interrogation périodique** (5 s pour les lots, 3 s pour l'amorçage), arrêtée quand plus rien ne tourne.

**Alternatives écartées** : un canal temps réel (SignalR/SSE) — inutile pour des tâches de plusieurs secondes à plusieurs minutes, et un état de plus à tenir ; stocker le coût par lot dans une colonne mise à jour par le worker — double écriture sans gain tant que la somme se lit en une requête.
