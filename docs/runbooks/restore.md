# Runbook — sauvegardes et restauration

Couvre NF-06 (sauvegardes quotidiennes, rétention 30 jours, restauration testée chaque mois), §9.7 et T-009. Fichiers : `deploy/staging/backup/`, `deploy/staging/postgres/`. Exemples avec le staging ; la production (T-010) suit la même procédure avec son propre `.env` et ses propres clés.

## Ce qui est sauvegardé

| Artefact | Fréquence | Emplacement (volume `backups`) | Sert à |
| --- | --- | --- | --- |
| Dump logique `pg_dump -Fc` de la base `onvoyage`, chiffré | tous les jours à `BACKUP_HOUR_UTC` (2 h UTC) | `dumps/onvoyage_<UTC>.dump.age` (+ `.sha256`) | restaurer dans une base neuve, copier vers un autre serveur |
| Base backup physique `pg_basebackup`, compressé et chiffré | chaque semaine (`BACKUP_BASEBACKUP_WEEKDAY`, 7 = dimanche) | `basebackups/<UTC>.tar.gz.age` | point de départ de la restauration à un instant donné |
| Segments WAL chiffrés | en continu (`archive_timeout` = 5 min, expédition chaque minute) | `wal/<segment>.age` | rejouer les écritures après le base backup |

- Rétention : `BACKUP_RETENTION_DAYS` (30). Le nettoyage suit chaque dump.
- La base unique contient aussi les files Wolverine (`wolverine_queues`) et les schémas de tous les services ; tout est dans le même dump.
- **Objectifs** : perte de données maximale (RPO) d'environ 6 minutes avec les WAL, 24 heures avec les seuls dumps ; durée de remise en service (RTO) visée : 2 heures, **non mesurée** à ce jour (à mesurer lors du premier exercice sur le VPS).
- Les fichiers du volume `media` (audio) ne sont pas dans PostgreSQL : ils sont régénérables par Factory tant que la base reste intacte ; leur sauvegarde passera avec le stockage objet UE.

## Clés (par nom, jamais en clair dans le dépôt)

| Nom | Où | Rôle |
| --- | --- | --- |
| `BACKUP_AGE_RECIPIENT` (variable GitHub `STAGING_BACKUP_AGE_RECIPIENT`) | `.env` du serveur | clé **publique** age (`age1…`) : chiffre. Le service refuse toute valeur qui n'en est pas une. |
| `AGE_IDENTITY_FILE` | poste de l'opérateur, au moment d'une restauration | **chemin** du fichier de clé privée age : déchiffre. Ne se trouve jamais sur le serveur. |

Générer la paire : `age-keygen -o onvoyage-backup.key` sur un poste de confiance. Conserver la clé privée en **deux exemplaires** hors du serveur (gestionnaire de mots de passe du propriétaire + copie hors ligne). Sans elle, les sauvegardes sont définitivement illisibles : vérifier sa présence à chaque test mensuel. Rotation : générer une nouvelle paire, changer `BACKUP_AGE_RECIPIENT`, redéployer ; garder l'ancienne clé privée tant qu'une sauvegarde chiffrée avec elle est dans la rétention.

## Test de restauration (mensuel, automatisé)

Le workflow `backup-restore-test.yml` s'exécute le 1er de chaque mois, à chaque modification des scripts et à la demande. Il lance sur un PostgreSQL jetable :

1. `deploy/staging/backup/test-restore.sh` : crée une base représentative (schémas des services, géométrie PostGIS, index trigramme, JSONB, `bytea`), la sauvegarde avec `backup.sh dump` (pg_dump → age), vérifie que le fichier est illisible sans clé, le restaure avec `restore.sh` dans une base vierge, puis compare le nombre de lignes de **chaque table** et le résultat d'une requête spatiale. Code de sortie non nul au moindre écart.
2. `deploy/staging/backup/test-pitr.sh` : exercice d'archivage WAL sur un cluster jetable (archive_command → chiffrement → base backup → restauration à un point nommé) ; les lignes écrites après le point doivent être absentes.

Pour tester une base existante en lecture seule : `SOURCE_DB=<nom> deploy/staging/backup/test-restore.sh`.

Sortie réelle de l'exécution du 2026-10-01 (PostgreSQL 16.14 local, PostGIS) :

```
dump done (124K)
backup: ov_restore_src_13419_20261001T144855Z.dump.age (123649 bytes, encrypted; first bytes: age-encryption.org/v1)
TABLE                                        SOURCE   RESTORED
catalog.poi                                    1200       1200 ok
discovery.interaction                           900        900 ok
factory.audio_asset                             300        300 ok
platform.account                                250        250 ok
wolverine_queues.envelope                         0          0 ok
spatial query (POIs within 5 km of 43.22N 5.37E): source=430 restored=430
RESULT: restore test PASSED

shipped 4 WAL segment(s)
recovered rows: 1500 (phases: after-backup-before-restore-point,before-backup)
RESULT: PITR drill PASSED
```

En plus du test automatique, **une fois par mois** l'opérateur restaure le dernier dump réel du staging dans une base à part (procédure A, sans basculer) et compare les comptes avec `SOURCE_DB`. Noter la date et la durée dans le journal d'exploitation.

## Procédure A — restaurer un dump dans une base neuve

À utiliser pour une corruption logique (mauvaise migration, suppression par erreur) ou pour dupliquer l'environnement.

1. Récupérer le dump voulu : `docker compose exec backup ls -lh /backups/dumps` ; copier le fichier et son `.sha256` sur le poste qui détient la clé privée (`docker compose cp backup:/backups/dumps/<fichier> .`).
2. Restaurer **à côté** de la base vivante, depuis un conteneur du service `backup` (il contient `restore.sh`, `psql`, `pg_restore`, `age`) :

   ```bash
   cd /opt/onvoyage   # ou deploy/staging
   docker compose run --rm --no-deps \
     --entrypoint /opt/onvoyage/restore.sh \
     -v "$PWD/onvoyage-backup.key:/key/age.key:ro" -v "$PWD/<fichier>:/in/dump.age:ro" -v "$PWD/<fichier>.sha256:/in/dump.age.sha256:ro" \
     -e AGE_IDENTITY_FILE=/key/age.key \
     backup /in/dump.age onvoyage_restored
   ```

   (Le conteneur tourne sous l'uid 999 : la copie de la clé privée montée ici doit lui être lisible ; la placer dans un répertoire temporaire et la supprimer ensuite. `restore.sh` refuse de toucher une base existante sans `--replace`, vérifie la somme de contrôle, déchiffre en flux — le clair n'est jamais écrit sur disque — puis lance `pg_restore --exit-on-error`.)
3. Vérifier : comparer les comptes de lignes des tables principales entre `onvoyage` et `onvoyage_restored`, par exemple `docker compose exec postgres psql -U onvoyage -d onvoyage_restored -c "select count(*) from catalog.poi"`.
4. Basculer, si la base vivante doit être remplacée :

   ```bash
   docker compose stop gateway platform-api catalog-api discovery-api factory-api factory-worker web-admin
   docker compose exec postgres psql -U onvoyage -d postgres -c "ALTER DATABASE onvoyage RENAME TO onvoyage_old" \
                                                       -c "ALTER DATABASE onvoyage_restored RENAME TO onvoyage"
   docker compose up -d --wait
   deploy/staging/smoke-test.sh https://<domaine>
   ```

   Garder `onvoyage_old` quelques jours, puis `DROP DATABASE`.
5. **Rejouer les suppressions de comptes** demandées depuis la date du dump (droit à l'effacement, F-22/T-507) : une restauration ressuscite des données que des voyageurs ont fait effacer. La suppression de compte n'est pas encore implémentée (voir `docs/PRIVACY.md`) ; dès qu'elle le sera, relever les événements `TravelerDeletionRequestedV1` postérieurs au dump et les réappliquer avant de rouvrir l'accès. Les sauvegardes elles-mêmes expirent après 30 jours (information à donner dans la politique de confidentialité).

## Procédure B — restauration à un instant donné (WAL)

Pour revenir à l'état d'avant un incident dont on connaît l'heure (jamais plus de 30 jours en arrière).

1. Choisir le dernier base backup **antérieur** à l'instant visé : `ls /backups/basebackups`.
2. Sur un serveur PostgreSQL 16 vierge (même image `postgis/postgis:16-3.4`), répertoire de données vide, avec la clé privée :

   ```bash
   age -d -i onvoyage-backup.key basebackups/<UTC>.tar.gz.age | gunzip | tar -x -C "$PGDATA"
   touch "$PGDATA/recovery.signal"
   cat >> "$PGDATA/postgresql.conf" <<'EOF'
   restore_command = 'age --decrypt --identity /key/age.key --output "%p" /backups/wal/%f.age'
   recovery_target_time = '2026-10-01 14:30:00+00'
   recovery_target_action = 'promote'
   EOF
   ```

   (Pour un point nommé : `recovery_target_name`, créé avec `select pg_create_restore_point('avant_migration')` — recommandé avant toute opération risquée.)
3. Démarrer PostgreSQL ; il rejoue les WAL jusqu'à la cible puis s'ouvre. Contrôler (`select now()`, comptes), puis suivre les étapes 4 et 5 de la procédure A (basculer, rejouer les suppressions).
4. Les segments encore présents dans `wal-incoming` (non chiffrés, en transit au plus quelques minutes) sont expédiés par le service `backup` ; si le serveur est perdu, ils le sont aussi.

## Procédure C — perte du VPS

1. Nouveau VPS préparé comme dans [DEPLOYMENT.md](../DEPLOYMENT.md) ; mettre à jour le DNS.
2. Récupérer les sauvegardes depuis la copie hors serveur (à configurer, H-003) — **les sauvegardes locales disparaissent avec le serveur**.
3. `docker compose up -d --wait postgres volume-init`, restaurer le dernier dump dans la base `onvoyage` (procédure A avec `--replace`), puis lancer le workflow `deploy-staging` : les migrations sont sans effet sur un schéma déjà à jour.
4. Régénérer l'audio du volume `media` si besoin (Factory) et vérifier avec le test de fumée.

## Surveillance et dépannage

| Symptôme | Action |
| --- | --- |
| Conteneur `backup` « unhealthy » | le battement `/backups/state/heartbeat` date de plus de 3 min : `docker compose logs backup` |
| « BACKUP_AGE_RECIPIENT … must be an age public key » | variable vide ou clé privée collée par erreur : corriger `.env` |
| `archive_command` échoue (journal PostgreSQL) | le volume `wal-incoming` n'appartient pas à l'utilisateur 999 : relancer `docker compose up volume-init` ; PostgreSQL conserve ses WAL tant qu'ils ne sont pas archivés (surveiller l'espace disque de `postgres-data`) |
| Disque plein | réduire `BACKUP_RETENTION_DAYS` ou agrandir le disque ; ne jamais supprimer `wal/` sans prendre un nouveau base backup |
| `checksum mismatch` | fichier corrompu à la copie : reprendre une autre sauvegarde |
| Aucun dump récent | `last-dump` dans `/backups/state` indique le dernier jour réussi ; relancer `docker compose exec backup /opt/onvoyage/backup.sh dump` |

Tout ce que ce runbook décrit pour le staging est **exécuté en CI par les deux scripts de test** ; les commandes `docker compose` de ce document n'ont pas pu être rejouées sans démon Docker et sont à valider lors du premier exercice sur le VPS.
