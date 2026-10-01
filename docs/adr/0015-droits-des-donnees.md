# ADR-0015 — Export et suppression des données (T-507)

Statut : accepté — MVP-0

## Contexte
F-22 impose un export (accès) et une suppression (effacement) couvrant tous les services qui détiennent des données voyageur.

## Décision
- **Platform orchestre**, les services répondent. Platform publie `TravelerDeletionRequestedV1` / `TravelerExportRequestedV1` vers la file de chaque service concerné (`Messaging:DataRightsSubscribers`) ; chacun répond sur la file `platform` (`TravelerDataDeletedV1`, `TravelerExportPartReadyV1`).
- **Services requis** par phase : `deletion.required_services` (config), surchargeable par `Platform:DeletionRequiredServices`. Un service non encore déployé ne bloque pas.
- **Suppression** : le compte passe en « suppression demandée », puis est purgé quand tous les accusés sont reçus. Seule une ligne anonyme est conservée dans `deletion_log` (date, services). Les comptes anonymes inactifs depuis 24 mois sont purgés par la même voie.
- **Export** : chaque service écrit sa partie JSON dans la zone privée `exports/` (`ExportStorage`, `Exports:Directory`, répertoire partagé en MVP-0, stockage objet ensuite) ; Platform assemble une archive ZIP, supprime les parties, l'archive expire après 24 h.
- **Événements idempotents** (`EventId`, `OccurredAt`) ; les handlers peuvent être rejoués sans effet.
- **Consentement statistiques** : le choix est enregistré par Platform (`ConsentChangedV1` → Insights) puis recopié localement (`AnalyticsConsent`) ; sans réponse, c'est un refus.

## Conséquences
- Ajouter un service détenant des données voyageur = brancher ses deux handlers, l'ajouter aux listes de routage et à `deletion.required_services`.
- L'export et la suppression ne sont pas testés de bout en bout avec Aspire ; les tests d'intégration Platform couvrent l'orchestration avec de vrais handlers Discovery et Factory.
