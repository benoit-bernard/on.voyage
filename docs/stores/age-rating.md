# Classification d'âge, catégories et déclarations de contenu

Brouillon du 2026-10-01. Les questionnaires officiels (Apple, IARC pour Google Play) changent de libellés ; ce document donne la **réponse de fond** à chaque thème, à reporter question par question au moment de la soumission.

## Résultat attendu

| Boutique | Résultat attendu | Remarque |
| --- | --- | --- |
| App Store | **4+** (tous publics) | À reconfirmer dans le questionnaire en vigueur (Apple a modifié ses tranches d'âge en 2025). |
| Google Play (IARC) | PEGI 3 / ESRB Everyone / USK 0 | Même réserve. |
| Public cible déclaré (Google) | 13 ans et plus [à confirmer] | L'app n'est **pas** conçue pour les enfants (pas de mode enfants avant la V2) : ne pas cocher « enfants » pour ne pas entrer dans la politique Familles. |

## Réponses par thème

| Thème du questionnaire | Réponse | Justification |
| --- | --- | --- |
| Violence, sexualité, nudité, langage cru, drogue, alcool, tabac, jeux d'argent, horreur, humour mature | **Aucun** | Contenu éditorial culturel sur des lieux (patrimoine, nature, histoire). Les histoires sont générées sous contrôle (§8) ; les sujets sensibles de l'histoire (guerres, épidémies) sont traités factuellement. [Contrôle : parcourir les 150 histoires du lot MVP-0 pour y vérifier le ton avant de répondre « aucun ».] |
| Contenu généré par les utilisateurs | **Non** | Pas de commentaires, avis, messagerie ni profils publics (§3.3 hors périmètre). Les voyageurs ne font que signaler une erreur (non publié). Les contenus de créateurs (MVP) sont référencés par lien, modérés par l'équipe. |
| Accès web illimité | **Non** | Pas de navigateur intégré ; les liens sortants (Wikipédia, vidéos) s'ouvrent dans le navigateur ou l'app du système. |
| Achats intégrés | **Non** en MVP-0 | Premium au MVP (F-17) : à revoir avec Billing. |
| Publicité | **Non** en MVP-0 et MVP | V1.1 : régie maison. |
| Localisation | **Oui, utilisée** | Position au premier plan, voir `permissions.md`. |
| Partage de la position avec d'autres utilisateurs | **Non** | Aucune fonction sociale entre voyageurs. |
| Contenu généré par IA | **Oui** (texte et voix de synthèse) | Transparence : mention « Voix générée par intelligence artificielle » dans le lecteur et avis à la première écoute (annexe G, F-06). Aucun contenu généré **à la demande** de l'utilisateur : tout est produit en amont par Factory, jamais sur le chemin d'une requête. Aucune discussion libre avec une IA. |
| Médicaments, santé, médical | **Non** | — |
| Messagerie, rencontres | **Non** | — |

## Catégories et classification

| Boutique | Principale | Secondaire |
| --- | --- | --- |
| App Store | Voyage | à décider (voir `listing-fr.md`) |
| Google Play | Voyages et infos locales | — (tags : « Guides de voyage », « Cartes et navigation » ne sont à cocher que si la console les propose et que la fiche les justifie) |

## Autres déclarations

| Sujet | Réponse |
| --- | --- |
| Chiffrement à l'exportation (Apple) | `ITSAppUsesNonExemptEncryption` = `false` (déjà dans `Info.plist`) : HTTPS standard uniquement. |
| Statut de commerçant (DSA, UE) | Apple et Google demandent de déclarer si l'éditeur est un « trader » et publient ses coordonnées dans l'UE. Décision et pièces du propriétaire (H-003). |
| Licences des données | Mention OpenStreetMap et Protomaps sur la carte (déjà dans l'app) ; sources et licences des histoires en pied de fiche. Voir `docs/LICENSING.md` quand il existera (§22). |
| Marques et droits | Aucun logo tiers dans les captures ; pas de marque de ville dans le nom de l'app. |
| Public « Apps conçues pour les enfants » / Programme Familles | Non. |
