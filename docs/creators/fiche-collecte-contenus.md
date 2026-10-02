# Fiche de collecte — profil, contenus, lieux et conseils

À remplir avec le créateur (ou par lui). Quatre tableaux correspondent aux quatre fichiers CSV de [modeles/](modeles/). Ouvrez-les avec Google Sheets, LibreOffice ou Excel (séparateur `;`, encodage UTF-8), enregistrez en **CSV UTF-8**. Une ligne dont la première cellule commence par `#` est un commentaire, ignorée. L'outil `tools/import-founder-creators` signale chaque erreur avec le fichier, la ligne et la colonne.

**À ne jamais mettre dans ces fichiers** : adresse e-mail, téléphone, adresse postale, identité civile du créateur (elle reste dans la feuille de suivi privée), ni les mêmes éléments pour un tiers. L'outil refuse un texte qui contient une adresse e-mail ou un numéro de téléphone.

Plusieurs valeurs dans une cellule : séparées par `|` (barre verticale).

## 1. `creators.csv` — une ligne par créateur

| Colonne | Obligatoire | Format et règle | Champ du service |
| --- | --- | --- | --- |
| `key` | oui | clé locale courte (`a-z`, `0-9`, `-`, `_`), sert à relier les autres fichiers | — |
| `handle` | oui | pseudonyme 3 à 30 caractères : lettres sans accent, chiffres, `.` et `_` ; ni point final, ni deux points de suite ; unique sans tenir compte de la casse | `handle` |
| `display_name` | oui | nom affiché, 80 caractères au plus | `displayName` |
| `bio` | non | 300 caractères au plus, aucune donnée personnelle | `bio` |
| `avatar_path` | non | chemin du fichier photo que nous hébergeons, sans espace (laisser vide au début) | `avatarPath` |
| `languages` | non | codes de 2 ou 3 lettres, 6 au plus : `fr\|en` | `languages` |
| `specialties` | pour publier | **1 à 5** codes de la taxonomie (annexe D du cahier, 74 codes) : `nature.coast\|history.local\|gastronomy.markets` | `specialties` |
| `destination_ids` | non | identifiants (GUID) de destinations ; laisser vide | `destinationIds` |
| `link_instagram`, `link_youtube`, `link_tiktok`, `link_website` | non | adresse `https://` de son profil ou site, 300 caractères au plus | `links` |
| `consent_document_ref` | pour publier | **référence du consentement signé** (`FONDATEUR-AAAA-NNN`), 200 caractères au plus | `documentRef` |
| `consent_accepted_at` | avec la référence | date de signature `AAAA-MM-JJ`, pas dans le futur | `acceptedAt` |
| `account_id` | non | GUID du compte du créateur ; laisser vide (voir README, « Écarts ») | `accountId` |
| `publish` | non | `oui` pour publier à l'import, sinon `non` (brouillon). Refusé sans consentement ni spécialité | — |

Codes de spécialité courants à Marseille : `nature.coast`, `nature.viewpoints`, `nature.cliffs_gorges`, `history.local`, `history.maritime`, `architecture.modern`, `culture.street_art`, `culture.museums`, `gastronomy.local_cuisine`, `gastronomy.markets`, `villages.fishing`, `outdoors.hiking`, `outdoors.water_sports`, `leisure.beaches`. Liste complète : cahier des charges, annexe D.

## 2. `contents.csv` — une ligne par contenu

| Colonne | Obligatoire | Format et règle |
| --- | --- | --- |
| `creator_key` | oui | la `key` du créateur |
| `url` | oui | adresse https d'une **vidéo ou publication publique** : YouTube (`watch?v=…`, `youtu.be/…`, `shorts/…`), Instagram (`/p/…`, `/reel/…`, `/tv/…`), TikTok (`/@compte/video/numéro`). Un profil ou une chaîne est refusé |
| `title` | oui | 200 caractères au plus |
| `caption_excerpt` | non | extrait de la légende, 500 caractères au plus |
| `cover_path` | non | chemin de la vignette fournie ou autorisée par le créateur, sans espace |
| `published_at` | non | `AAAA-MM-JJ` |
| `duration_seconds` | non | secondes ou `mm:ss` |
| `kind` | non | `video`, `photo`, `carousel` ou `article` (déduit de l'adresse si vide) |
| `is_commercial` | non | `oui` si le contenu est sponsorisé : affiche « Publicité » ; sinon `non` |
| `chapters` | non | moments d'une vidéo qui parlent d'un lieu : `0:00 Introduction\|2:15 Vallon des Auffes\|6:40 Malmousque` (titre de 100 caractères au plus, horodatages distincts, dans la durée) |

Un même contenu ne peut figurer qu'une fois (deux écritures du même lien YouTube sont reconnues comme un seul contenu).

## 3. `place_links.csv` — une ligne par association contenu → lieu

| Colonne | Obligatoire | Format et règle |
| --- | --- | --- |
| `creator_key` | oui | la `key` du créateur |
| `place` | oui (ou `poi_id`) | nom du lieu **tel qu'il est dans le catalogue** (la casse et les accents n'importent pas ; un nom approximatif est signalé, jamais deviné) |
| `poi_id` | non | identifiant (GUID) du lieu, si le nom est ambigu |
| `content_url` | non | adresse d'un contenu **déjà listé** dans `contents.csv` pour ce créateur ; vide = le créateur recommande le lieu sans contenu précis |
| `start_time` | non | `mm:ss` ou secondes : l'instant de la vidéo où il parle du lieu ; suppose `content_url` et reste dans la durée |
| `status` | non | `validated` (par défaut : le créateur est d'accord), `proposed` ou `rejected` |

Les lieux disponibles sont ceux du catalogue publié (36 lieux du snapshot de Marseille au départ, `data-pipeline/marseille/pois.json`). Un lieu absent du catalogue ne peut pas être associé : le noter dans la feuille de suivi (idée de lieu à ajouter).

## 4. `tips.csv` — un conseil par lieu

| Colonne | Obligatoire | Format et règle |
| --- | --- | --- |
| `creator_key` | oui | la `key` du créateur |
| `place` / `poi_id` | l'un des deux | comme ci-dessus |
| `text` | oui | **280 caractères au plus**, première personne, utile et concret (« Venez au coucher du soleil, côté ouest »). Pas de note chiffrée, pas de prix, pas de coordonnées, aucune promotion d'accès à un site fragile |

Un seul conseil par lieu et par créateur. Un conseil sur un lieu sans association crée automatiquement une association « conseil seul ».

## Objectif de collecte par créateur

1 profil complet, **au moins 3 lieux** associés, 1 à 5 contenus, 1 à 3 conseils. Mieux vaut peu et exact que beaucoup : le créateur relit tout avant publication.

## Avant d'envoyer au développeur / à l'import

1. `python3 tools/import-founder-creators/import_founder_creators.py validate <dossier>` : zéro erreur.
2. Le créateur a relu les quatre fichiers et dit « c'est bon » par écrit.
3. La référence du consentement signé est dans `creators.csv`.
