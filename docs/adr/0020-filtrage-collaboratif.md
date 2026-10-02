# ADR-0020 — Filtrage collaboratif par voisins, job de précalcul et `cf_score` (T-504)

- Statut : accepté. Réalise le §6.5 ; complète l'ADR-0011 (Discovery) dont il lève la réserve sur « pgvector arrive avec T-504 ».

## Ce qui est fait
- **Voisins** : les `K` = 50 voyageurs dont le vecteur est le plus proche en similarité cosinus, parmi ceux dont `profile_depth ≥ 10` et actifs dans les 365 derniers jours. Seules les similarités **positives** comptent (quelqu'un qui aime le contraire n'est pas un voisin) ; égalités départagées par identifiant, donc un second passage donne les mêmes voisins.
- **Score** (§6.5) : `CF = Σ sim·r / (Σ|sim| + λ)`, `λ = 5`, `r` = la note du voisin pour le lieu (`discovery.poi_rating`, −1 si lieu écarté), `support` = nombre de voisins qui l'ont noté. Fonction pure `CollaborativeFiltering` dans `OnVoyage.Recommendation.Engine` (partagée avec l'app pour le hors ligne).
- **Job** : `RecomputeCfScoresCommand`, une commande Wolverine lancée toutes les 6 h (`Discovery:Cf:IntervalHours`, premier passage 2 min après le démarrage, après le démarrage complet de l'hôte) et à la main par `POST /admin/cf-scores/recompute`. Pour chaque voyageur actif dont le profil a au moins 5 points (au-dessous : démarrage à froid, `w_cf = 0`, §6.7), il remplace ses lignes de `discovery.cf_score` (200 meilleurs scores par destination) dans une transaction, puis supprime les lignes des voyageurs qui ne sont plus éligibles. Idempotent.
- **Lecture en ligne** : `RankingContext` charge les scores du voyageur (jamais ceux de la cohorte témoin, F-03) et les passe au moteur comme `Candidate.Collaborative`. `/me/candidates` (`baseScore`) et les listes en tiennent compte ; `/me/cf-scores` renvoie `cf`, `support` et `creatorSignal` pour le hors ligne.
- **Mélange** (§6.5, §6.7) : `w_cf_effectif = w_cf · min(1, support / 10) · CF01`, `CF01 = (CF + 1) / 2`. L'explication « Des voyageurs aux goûts proches l'ont adoré » (code `collaborative`) vient en troisième position (§6.9), quand `w_cf_effectif·CF01` dépasse 30 % du score et qu'aucune catégorie aimée n'explique déjà la proposition.

## Écarts assumés
1. **pgvector / HNSW non utilisés.** Le cahier demande un index HNSW (`vector_cosine_ops`). L'extension n'est ni dans l'image `postgis/postgis:16-3.4` de l'AppHost, ni dans la base de test, ni dans le PostgreSQL 16 local ; l'imposer casserait le démarrage local et la CI pour un gain nul à l'échelle actuelle. Les vecteurs restent des `real[]` (ADR-0011) et la recherche est **exacte** (cosinus contre chaque voyageur éligible, en mémoire, le vivier étant lu une fois par passage), derrière le port `INeighborSearch`. Le coût d'un passage complet est de l'ordre de `N² × 74` multiplications (7,4 milliards pour 10 000 voyageurs éligibles : de l'ordre de la dizaine de secondes ; **estimation, non mesurée** — aucun jeu de cette taille n'a été essayé). Au-delà, ou quand l'image portera pgvector, une implémentation `ORDER BY vector <=> @v LIMIT 50` (index HNSW) remplace `ExactCosineNeighborSearch` sans toucher au reste ; le test d'intégration du job reste valable. Voir aussi `docs/DATABASE.md`.
2. **Poids non utilisé.** Le cahier ne dit pas où va la part de `w_cf` que `min(1, support/10)` laisse inutilisée. Elle va **à parts égales à `w_imp` et `w_q`**, comme le cahier le fait au démarrage à froid : la somme des poids reste constante, le contenu domine quand les voisins en disent peu, et sans aucune donnée collaborative le classement est exactement celui de la version précédente (testé).
3. **Meilleurs scores** : les 200 gardés par destination sont les plus **hauts**. Un lieu que les voisins ont mal noté n'a donc de score que s'il fait partie de ces 200 ; sinon il n'a pas de signal collaboratif (il reste sous le coup des autres termes).

## Vie privée (§16, `docs/PRIVACY.md`)
- Le calcul reste interne à Discovery. **Aucun voisin n'est stocké, journalisé ni renvoyé** : `cf_score` ne contient que (voyageur, lieu, destination, score, nombre de voisins, date), ce que vérifie un test sur le schéma.
- **Seuils (k-anonymat)** : un lieu noté par moins de `Discovery:Cf:MinSupport` = 3 voisins n'a pas de score (il refléterait ce qu'une seule ou deux personnes ont dit) ; si le vivier de voisins éligibles compte moins de `Discovery:Cf:MinPool` = 20 voyageurs (le même k = 20 que les statistiques créateur et annonceur), aucun score n'est produit et ceux d'avant sont supprimés. Ces deux seuils sont des paramètres ⚙️ du service ; le cahier ne chiffre que `K`, `λ` et le seuil de 10 voisins du poids.
- La cohorte témoin (F-03) ne reçoit ni ne contribue différemment : elle ne reçoit pas de score, mais ses notes peuvent servir aux autres (comme ses interactions servent déjà la matrice d'affinité).
- Une donnée personnelle de plus : `discovery.cf_score`, inscrite au registre, supprimée avec le compte (cascade), incluse dans l'export (`collaborativeScores`), recalculée ou supprimée à chaque passage.

## Vérification
Moteur : 18 tests (cosinus, voisins, formule, seuils, poids selon le support, démarrage à froid, somme des poids, explication). Discovery : 8 tests unitaires du job (population trop petite, cas d'une place notée par 2 voisins, démarrage à froid, plafond de 200, déterminisme, nettoyage) et 10 tests d'intégration sur PostgreSQL (voisin au profil identique qui influence le score, support faible = poids réduit, cohorte témoin, suppression en cascade et export, schéma, job périodique, accès admin).

## Non fait
- L'app (T-620) ne lit pas encore `support` ; elle ignore le champ.
- Pas de parallélisme du passage (séquentiel, sans état partagé : à ajouter si le passage dépasse l'intervalle).
- Pas d'évaluation hors ligne de la qualité des recommandations collaboratives (pas de jeu de données réel).
