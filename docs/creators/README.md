# H-009 — Recruter 5 à 10 créateurs fondateurs marseillais

Tâche humaine H-009 (cahier des charges §24.2, F-26, D-16). Durée estimée : 6 à 10 h réparties sur 2 à 4 semaines (les créateurs répondent lentement). Elle fournit les **données** de T-1202 : sans elle, le bloc « Vu par les créateurs » et les pages `/@handle` restent vides.

Un créateur fondateur est créé **par l'administrateur, avec son consentement écrit** : ce consentement vaut acceptation, `terms_version = "fondateur"`, et la **référence du document signé** est stockée avec la date (ADR-0016). Sans cette référence et cette date, le service refuse la publication (`terms_required`).

## Les pièces

| Pièce | Fichier | Usage |
| --- | --- | --- |
| Messages de recrutement (e-mail, message privé, relance, remerciement) | [messages-de-recrutement.md](messages-de-recrutement.md) | premier contact et suivi |
| Présentation du programme (une page) | [programme-createurs-fondateurs.md](programme-createurs-fondateurs.md) | à joindre au premier message |
| Consentement fondateur (formulaire à signer) | [consentement-fondateur.md](consentement-fondateur.md) | **à relire par un juriste** avant le premier envoi |
| Notice d'information RGPD | [notice-rgpd-createurs.md](notice-rgpd-createurs.md) | à remettre avec le consentement |
| Fiche de collecte des contenus et des conseils | [fiche-collecte-contenus.md](fiche-collecte-contenus.md) | explique les quatre CSV au créateur |
| Modèles CSV | [modeles/](modeles/) : `creators.csv`, `contents.csv`, `place_links.csv`, `tips.csv` | ce que le créateur (ou vous, avec lui) remplit |
| Jeu de démonstration (fictif) | [modeles/demo/](modeles/demo/) | pour essayer l'outil ; **refusé par `apply`** |
| Feuille de suivi | [suivi-recrutement.modele.csv](suivi-recrutement.modele.csv) | à copier **hors du dépôt** |
| Importeur | `tools/import-founder-creators/` | valide les CSV, affiche ou exécute les appels d'administration |

## Parcours pour un créateur

1. **Repérer** (liste de 15 à 20 pour en signer 5 à 10) : créateurs dont les contenus montrent Marseille ou la région, actifs, dont le ton colle à ON.VOYAGE (lieux moins fréquentés, respect des sites fragiles). Aucune donnée à leur sujet dans le dépôt : tout reste dans la feuille de suivi privée.
2. **Contacter** avec un message de [messages-de-recrutement.md](messages-de-recrutement.md) + la page du programme.
3. **Échanger** (appel de 20 minutes) ; répondre aux questions avec la notice RGPD.
4. **Faire signer** le consentement fondateur (signature manuscrite scannée ou signature électronique), le ranger dans le dossier privé, noter la **référence** (`FONDATEUR-AAAA-NNN`) dans la feuille de suivi.
5. **Collecter** : le créateur renseigne le profil, ses contenus (liens), les lieux qu'il en tire, ses conseils (fiche de collecte). Pas de fichier vidéo ni photo : seulement des liens.
6. **Valider** puis **importer** (section suivante) ; **relire** la page du créateur dans le back-office, la lui faire valider ; **publier**.
7. Informer le créateur de la publication et de la manière de se retirer.

Objectif de la tâche : 5 à 10 créateurs publiés, chacun avec au moins 1 contenu, 3 lieux associés et un conseil. Données **réelles** uniquement dans le dossier privé et dans la base de staging/production ; **rien de réel dans le dépôt** (règle du dépôt : pas de données de personnes réelles, voir `docs/PRIVACY.md`).

## Où rangent-on quoi

| Élément | Où | Dans le dépôt ? |
| --- | --- | --- |
| Consentements signés (PDF) | dossier privé chiffré du propriétaire produit (`docs/creators/prive/` en local est ignoré par git) | **non** |
| Feuille de suivi (noms, e-mails, avancement) | tableur privé | **non** |
| CSV remplis (pseudonymes, bios, liens publics) | dossier privé ; copie locale dans `docs/creators/prive/` | **non** (ils contiennent des données de personnes réelles ; la base est leur lieu) |
| Modèles et jeu de démonstration | `docs/creators/modeles/` | oui |

Le service ne stocke que la **référence** du document, jamais le document (`docs/PRIVACY.md`, ligne « Profil d'un créateur fondateur »).

## Importer (outil `tools/import-founder-creators`)

Python 3.9 ou plus, aucune installation. Les quatre CSV sont dans un dossier (`creators.csv` obligatoire ; `contents.csv`, `place_links.csv`, `tips.csv` facultatifs). UTF-8, séparateur `;` ou `,` reconnu automatiquement, une ligne dont la première cellule commence par `#` est ignorée.

```bash
D=docs/creators/prive/lot-1

# 1. Valider (hors ligne) : erreurs avec fichier:ligne et colonne ; code de sortie 1 s'il y en a
python3 tools/import-founder-creators/import_founder_creators.py validate $D

# 2. Voir les appels qui seraient faits (aucune écriture, aucun jeton nécessaire)
python3 tools/import-founder-creators/import_founder_creators.py dry-run $D
python3 tools/import-founder-creators/import_founder_creators.py dry-run $D --format curl --base-url https://staging.on.voyage

# 3. Pareil, mais en interrogeant le vrai serveur pour les lectures (créateurs existants, noms de lieux) : détecte un lieu mal orthographié
export ONVOYAGE_ADMIN_TOKEN=...      # voir `login` ci-dessous
python3 tools/import-founder-creators/import_founder_creators.py dry-run $D --read-online --base-url https://staging.on.voyage

# 4. Écrire
python3 tools/import-founder-creators/import_founder_creators.py apply $D --base-url https://staging.on.voyage
```

Jeton d'administrateur (valable 60 minutes, jamais passé sur la ligne de commande ni écrit dans un fichier) :

```bash
python3 tools/import-founder-creators/import_founder_creators.py login --base-url https://staging.on.voyage --email <adresse admin>
# saisir le code reçu par e-mail ; copier la ligne `export ONVOYAGE_ADMIN_TOKEN=...` affichée
```

Le compte doit être celui de `STAGING_BOOTSTRAP_ADMIN_EMAIL` (rôle `admin`). L'adresse de base est celle du **Gateway** (le domaine public du staging ou de la production) : les appels vont à `/api/creators/v1/admin/**`, route protégée par la politique `admin`.

### Ce que fait `apply`, dans l'ordre, pour chaque créateur

| Étape | Appel (via le Gateway) | Remarque |
| --- | --- | --- |
| Chercher le pseudonyme | `GET /api/creators/v1/admin/creators?search=<handle>` | déjà présent : le profil n'est pas touché (`--update-profiles` pour l'écraser) |
| Créer le profil | `POST /api/creators/v1/admin/creators` | refus `409` si le `@handle` est pris (insensible à la casse) |
| Enregistrer le consentement fondateur | `PUT /api/creators/v1/admin/creators/{id}/consent` `{documentRef, acceptedAt}` | donne `terms_version = fondateur` |
| Rattacher le compte (si `account_id`) | `PUT …/{id}/account` | voir « Écarts » |
| Ajouter les contenus | `POST …/{id}/contents` | contenu déjà présent (même lien canonique) : ignoré |
| Associer les lieux | `GET …/places?query=` pour trouver `poiId` (nom exact, sans tenir compte des accents ni de la casse), puis `POST …/{id}/place-links` | un lieu inconnu ou ambigu est signalé, **jamais deviné** |
| Écrire les conseils | `PUT …/{id}/tips/{poiId}` | |
| Publier (si `publish = oui`) | `POST …/{id}/publish` | refus `terms_required` sans consentement, `specialty_required` sans spécialité |

Le résultat tient en trois listes (fait / ignoré / problème). On peut rejouer la commande après avoir corrigé un CSV : ce qui existe n'est pas dupliqué. Un créateur en `publish = non` reste en brouillon : le publier après relecture dans `/admin/creators/{id}` (le bouton affiche la raison s'il est bloqué).

### Écarts à connaître

- **Rattachement du compte (`account_id`)** : il faut l'identifiant de voyageur du compte du créateur (renvoyé à sa connexion par code e-mail). Aucune route d'administration ne le retrouve à partir d'une adresse e-mail aujourd'hui ; laissez la colonne vide : le profil est publiable sans compte (le rôle `creator` de Platform ne sert qu'à l'espace Studio, T-1206).
- **Destinations (`destination_ids`)** : l'API attend des identifiants (GUID) de destinations, pas des slugs ; colonne facultative.
- **Photo de profil et vignettes** : `avatar_path` et `cover_path` sont des chemins vers des fichiers que **vous** hébergez (aucune copie de média du créateur n'est faite par l'importeur) ; laissez vide pour l'instant.
- Le service n'a pas de route de suppression d'un créateur : en cas d'erreur, corriger par le back-office.
- L'outil n'a été exécuté que contre un serveur simulé (tests) : le premier `apply` doit viser le **staging** avec un seul créateur.

## Définition de « terminé »

- 5 à 10 consentements signés, référencés dans la feuille de suivi, rangés hors dépôt.
- `validate` sans erreur, `apply` sans problème sur le staging, puis sur la production (T-010) quand elle existera.
- Chaque créateur a relu sa page et donné son accord pour la publication (message de confirmation).
- Aucune donnée de personne réelle commitée.

Tests de l'outil : `python3 -m unittest discover -s tools/import-founder-creators -v`.
