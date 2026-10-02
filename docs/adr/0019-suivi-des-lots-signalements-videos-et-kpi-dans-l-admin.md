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

## 2. Boîte des signalements (T-405)

**Ce qui existait** : `story_report`, file `GET /admin/reports` (par histoire, tri par date, par nombre de lecteurs ou par lieu), clôture `Handled`/`Dismissed` avec note, « Suspendre et ouvrir une correction » (nouvelle version de l'histoire, signalements clos), suspension automatique à trois lecteurs distincts, jamais d'identité de lecteur dans la file.

**Ce qui est ajouté**
- **Type de signalement.** L'app compose déjà `[Kind] texte` (`ReportLabels.Compose`) : le serveur lit ce préfixe (`ReportKinds.Parse`) au lieu d'ajouter une colonne et un champ d'API. Pas de préfixe connu (ancienne app, appel à la main) = fait inexact, c'est ce que c'était avant. Un préfixe inconnu devient « Autre » et garde son texte.
- **Seuil de suspension (F-20)** : seuls les signalements « fait inexact » comptent (`CountDistinctReportersAsync(…, inaccurateFactOnly)`). Cinq lecteurs qui disent « la prononciation est fausse » ne retirent plus l'histoire du Catalog ; ils restent dans la boîte pour l'éditeur.
- **Boîte** : filtre par type (`?kind=`, 400 si inconnu), décompte par type et par histoire, lien vers l'histoire **et** vers le lieu, type affiché sur chaque remarque (le texte est montré sans le préfixe).
- **Audit** : toutes les écritures de l'admin Factory passent par `AuditFilter` (journal local + `AdminActionRecordedV1` vers Platform). Les décisions « traité » / « écarté » y figurent avec leur note ; un test d'intégration vérifie la ligne locale et un autre l'arrivée dans le journal de Platform, y compris pour une histoire inconnue (404).

**Écarts au cahier** : la route voyageur reste `POST /stories/{id}/reports` (pas `POST /reports` avec `poi_id`), et l'extrait sélectionné facultatif n'existe pas (le texte libre de 500 caractères le remplace). Le correctif « corriger → nouvelle version » est celui de T-308 (`OpenCorrectionCommand`).

## 3. Vidéos YouTube (T-407)

**Ce qui existait** : `GET /admin/videos/search` (clé `YouTube:ApiKey` du serveur, jamais l'app), sélection de 0 à 2 vidéos par lieu, titre/chaîne/vignette relus chez YouTube (jamais pris de la requête), vignette copiée sur notre stockage, `external_link` dans le Catalog (le lieu publié est republié), sélecteur dans la fiche du lieu.

**Ce qui est ajouté : la gestion du quota.** L'API coûte 100 unités par recherche et 1 par consultation sur 10 000 par jour et par clé. Avant, une recherche de trop échouait en `502`, sans explication.
- **Comptage** (`youtube_usage`, un compteur par jour de quota, minuit heure du Pacifique) : une recherche ou une sélection qui dépasserait le quota est refusée **avant** l'appel (`429 youtube_quota_exhausted`, avec l'heure de remise à zéro). Si YouTube répond lui-même 403 `quotaExceeded`/`dailyLimitExceeded`, la journée est marquée pleine. Un autre 403 (clé refusée, API désactivée) reste une erreur de fournisseur : ce n'est pas un quota.
- **Cache** (`youtube_search`, 24 h) : la même recherche (casse et espaces ignorés) ne coûte rien. Une recherche en échec n'est ni comptée ni mise en cache.
- **Interface** : la ligne « Quota YouTube du jour » (unités, recherches possibles, remise à zéro) dans la fiche du lieu et sur la nouvelle page `/admin/videos`, qui liste toutes les vidéos choisies par lieu et destination, avec retrait. Liens sortants (`rel="noopener noreferrer"`), aucune intégration.
- Le comptage est tenu par nous, pas relu chez YouTube (l'API n'expose pas le quota restant) : si la clé sert à autre chose, le compteur sous-estime ; le 403 de YouTube reste le dernier garde-fou.

## 4. Tableau de bord des indicateurs (T-406)

**Ce qui existait** : `/admin/kpis` lisait `GET /api/insights/v1/kpis` (période, destination, cohorte) et affichait le catalogue du §26 avec cible, échantillon minimal et verdict (atteinte, non atteinte, échantillon trop petit, suivi, pas de données), la tendance du KPI stratégique par tranche de profondeur, et un message quand Insights n'est pas branché.

**Ce qui est ajouté**
- **Un défaut corrigé** : la liste des cohortes proposait `personalised`, qu'Insights refuse (400, il attend `personalized`) : choisir « Pour vous » ne pouvait jamais fonctionner. Un test compare les choix de la page à `Cohorts.All` d'Insights.
- **Comparaison des cohortes** : sans cohorte choisie, la page interroge Insights trois fois (toutes, `personalized`, `control`) et affiche, indicateur par indicateur, les deux valeurs avec leur échantillon et l'écart (points pour un pourcentage). Le ratio central en est exclu (il compare déjà les deux cohortes). Un indicateur sans valeur dans aucune des deux n'apparaît pas ; une cohorte témoin vide est signalée.
- **Filtres** : périodes usuelles (7, 30, 90 jours), destination, cohorte ; une période de plus de 400 jours est refusée avant l'appel (c'est la limite d'Insights).
- **Export CSV** sans JavaScript ni route de plus : un lien `data:` (UTF-8 avec BOM, décimales à point) généré depuis ce qui est affiché, une ligne par indicateur et cohorte (`indicateur, famille, libellé, cohorte, valeur, échantillon, cible, état`). Il complète `GET /kpis/export` d'Insights, qui exporte les composantes journalières brutes.
- **États vides** : Insights absent ; période sans événement (message distinct selon qu'une destination est filtrée : Insights ne rattache pas encore les événements à une destination, filtrer donne donc un tableau vide) ; indicateur sans donnée ; échantillon trop petit.
- **Tous les indicateurs du §26** : le catalogue compte les 22 indicateurs, dont trois ajoutés pour les créateurs (taux de clic du bloc « Vu par les créateurs », taux de suivi, installations attribuées) que **Insights calcule désormais** à partir de `creator_card_viewed`, `creator_content_opened`, `creator_profile_viewed`, `creator_followed` et `install_attributed`. Six indicateurs ne viennent pas d'Insights et sont affichés « Pas encore branché » avec leur source : écoutes sur lieux pépites (Catalog), clics « Alternative » (la surface n'est pas distinguée), signalements pour 100 écoutes (Factory), latence P95 (observabilité), créateurs actifs et lieux avec contenu créateur (Creators, Catalog). Un test garantit que le catalogue contient exactement ce que `KpiCalculator` calcule, plus ces six-là.

**Limites** : pas de graphique d'évolution dans le temps (le contrat d'Insights renvoie des totaux de période) ; la comparaison coûte trois appels ; les six indicateurs « pas encore branchés » restent à alimenter.
