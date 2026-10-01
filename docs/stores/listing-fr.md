# Fiche store — version française (brouillon)

Statut : brouillon du 2026-10-01 pour la tranche MVP-0 (Marseille, français). Les limites de longueur sont celles de l'App Store Connect et de la Google Play Console ; la colonne « Long. » est vérifiée par `docs/stores/check-lengths.py`. Les passages entre crochets `[…]` sont des **points à confirmer par le propriétaire** avant la soumission.

## Champs communs

| Champ | Limite | Valeur | Long. |
| --- | --- | --- | --- |
| Nom de l'app (Apple et Google) | 30 | ON.VOYAGE | 9 |
| Sous-titre (Apple) | 30 | Guide audio qui vous connaît | 28 |
| Texte promotionnel (Apple, modifiable sans nouvelle version) | 170 | Marseille se raconte à voix haute : des histoires audio choisies selon vos goûts, et les lieux que les foules n'ont pas trouvés. Sans compte, sans pub, sans traqueur. | 166 |
| Description courte (Google) | 80 | Des histoires audio sur les lieux autour de vous, choisies selon vos goûts. | 75 |
| Mots-clés (Apple, séparés par des virgules, sans espace) | 100 | guide audio,voyage,Marseille,balade,histoire,patrimoine,visite,carte,tourisme,hors des sentiers | 95 |
| Catégorie principale | — | Apple : Voyage. Google : Voyages et infos locales. | — |
| Catégorie secondaire (Apple) | — | Navigation [ou Style de vie ; Navigation suppose un guidage, à éviter si la fiche ne promet pas d'itinéraire] | — |
| Langue principale | — | Français (France) | — |
| URL d'assistance | — | `https://on.voyage/assistance` [à créer] | — |
| URL de confidentialité | — | `https://on.voyage/confidentialite` [à créer, voir `docs/PRIVACY.md`] | — |
| URL marketing (Apple) | — | `https://on.voyage` | — |
| E-mail de contact (Google) | — | `contact@on.voyage` [à confirmer] | — |
| Droits d'auteur (Apple) | — | 2026 [raison sociale de l'éditeur] | — |

## Description complète (limite 4000, App Store et Google Play)

```text
ON.VOYAGE est un guide de voyage audio qui apprend ce que vous aimez et vous raconte les lieux qui comptent pour vous, plutôt que les mêmes dix incontournables.

À Marseille, ouvrez l'app, écoutez quelques extraits, et découvrez une sélection « Pour vous » : architecture, histoire, nature, mer, vie de quartier. Plus vous écoutez, plus elle vous ressemble. Un avis d'un geste (j'aime, bof, pas pour moi) suffit à l'affiner.

CE QUE VOUS POUVEZ FAIRE
• Écouter l'histoire d'un lieu en quelques minutes, écran verrouillé si vous le souhaitez, avec vitesse de lecture et retour de 10 secondes.
• Parcourir la carte des lieux autour de vous et filtrer par centre d'intérêt, par lieux moins fréquentés ou par histoires disponibles.
• Activer le mode découverte : l'app ouvre le récit du lieu devant lequel vous passez, à pied, l'application au premier plan.
• Garder vos envies et recevoir un rappel quand vous passez près d'un lieu enregistré.
• Préparer votre visite avec « Marseille pour vous » et « Que visiter ? ».
• Lire la transcription de chaque histoire et en savoir plus par des liens vers Wikipédia et des vidéos (ils s'ouvrent hors de l'app).

DES LIEUX MOINS FRÉQUENTÉS, DU TOURISME PLUS RESPECTUEUX
ON.VOYAGE met en avant les lieux moins connus (« Moins fréquenté, tout aussi beau ») et ne déclenche jamais automatiquement le récit d'un site signalé comme fragile.

VOTRE VIE PRIVÉE D'ABORD
• Aucun compte obligatoire : l'app fonctionne dès l'ouverture avec une session anonyme. Un compte facultatif se crée avec votre e-mail et un code à 6 chiffres, sans mot de passe.
• Votre position sert à trouver les lieux proches ; elle n'est jamais enregistrée sur nos serveurs.
• Aucune publicité, aucun traceur publicitaire, aucun outil d'analyse ou de rapport de plantage de sociétés tierces.
• Les statistiques d'usage ne sont envoyées que si vous les acceptez, et vous pouvez changer d'avis à tout moment.
• Hébergement en Europe.

TRANSPARENCE SUR L'INTELLIGENCE ARTIFICIELLE
Les histoires sont rédigées à partir de sources vérifiées (Wikipédia, Wikidata, OpenStreetMap et sources officielles, avec leurs licences) puis relues selon des contrôles automatiques et éditoriaux. Elles sont lues par une voix de synthèse, signalée dans le lecteur.

Données cartographiques © contributeurs OpenStreetMap, Protomaps.
Première destination : Marseille. D'autres suivront.
```

Notes de rédaction :

- Les fonctions listées sont celles du périmètre MVP-0 de la matrice §3.2 et du code au 2026-10-01 : mode découverte au **premier plan, écran actif** seulement (l'arrière-plan est MVP) ; pas de recherche, pas de hors-ligne, pas de Premium, pas de mode voiture. Les ajouter à la description **uniquement quand elles sont livrées**.
- « Aucune publicité » est vrai pour le MVP-0 et le MVP ; à retirer ou nuancer quand la régie maison (V1.1) sera activée. Le reste de la section vie privée est un engagement permanent (§1.2, D-02).
- « Sans compte, sans pub, sans traqueur » du texte promotionnel suit la même règle.
- Fonctions volontairement non annoncées bien que présentes côté serveur : le signalement d'une erreur (la route existe, l'écran n'existe pas encore dans l'app), l'export et la suppression des données (F-22, pas encore implémentés).
- Les stores exigent que la fiche ne promette rien que l'app ne fait pas : relire cette liste contre le build soumis.

## Nouveautés de la version (App Store « Notes de version » / Google « Nouveautés »)

```text
Première version de test : guide audio pour Marseille, sélection « Pour vous », carte, mode découverte au premier plan, envies et rappels de proximité.
```

## Notes pour la relecture

Voir `review-notes.md` (accès, position simulée, voix de synthèse).
