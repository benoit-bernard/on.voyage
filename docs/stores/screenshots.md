# Captures d'écran et visuels — liste de prises de vue

Brouillon du 2026-10-01. Les tailles exactes exigées par les boutiques évoluent : **vérifier dans App Store Connect et Play Console le jour de la préparation**. Les valeurs ci-dessous sont celles à viser.

## Formats

| Boutique | Appareil | Taille de référence | Nombre |
| --- | --- | --- | --- |
| App Store | iPhone 6,9" (obligatoire, ou 6,5") | 1320 × 2868 px (portrait) | 3 à 10 |
| App Store | iPad 13" (obligatoire : l'app déclare `UIDeviceFamily` 1 et 2) | 2064 × 2752 px | 3 à 10 |
| Google Play | Téléphone | 1080 × 1920 px minimum, rapport de 9:16 à 16:9 | 2 à 8 (viser 8) |
| Google Play | Tablette 7" et 10" (recommandé) | 1200 × 1920 / 1600 × 2560 px | jusqu'à 8 chacune |
| Google Play | Visuel de présentation | 1024 × 500 px | 1 |
| Google Play | Icône | 512 × 512 px | 1 (source : `Resources/AppIcon`) |
| App Store | Icône | 1024 × 1024 px sans transparence | 1 |

Langues : un jeu en français maintenant ; un jeu en anglais avec le lancement EN (légendes à traduire, mêmes écrans).

## Préparation

- Build de test branché sur le staging avec les **données de démonstration de Marseille** (`Catalog__SeedDemoData=true`) ou, mieux, les 150 histoires publiées.
- Position simulée : Vieux-Port de Marseille, 43,2951 N 5,3744 E (iOS : Xcode ▸ Debug ▸ Simulate Location ; Android : émulateur ▸ Extended controls ▸ Location).
- Barre d'état propre : 9:41, batterie pleine, réseau complet, pas de notification.
- Profil de démonstration : onboarding terminé avec 5 extraits (goûts « histoire » et « mer »), 3 lieux enregistrés, 2 histoires écoutées.
- **Aucune donnée réelle** : pas d'e-mail réel (compte non créé ou adresse `demo@example.org`), pas de prénom, pas de visage identifiable dans les photos de lieux.
- Alterner thème clair et thème sombre (au moins 2 captures sombres) pour montrer que les deux existent (NF-09).
- Texte des légendes : court, sans promesse que le build n'honore pas (voir `listing-fr.md`, notes de rédaction).

## Liste de prises de vue (téléphone, 8 captures)

| # | Écran (route) | Ce qu'il doit montrer | Légende FR | Légende EN |
| --- | --- | --- | --- | --- |
| 1 | Accueil « Pour vous » (`/`) | Sélection personnalisée, au moins une carte « Pépite · moins fréquenté », le pourcentage ou l'explication « Pourquoi » s'ils existent | Des lieux choisis pour vous | Places picked for you |
| 2 | Onboarding (`/onboarding`) | Un extrait audio en cours, boutons j'aime / pas pour moi | Dites ce que vous aimez en 30 secondes | Tell us what you like in 30 seconds |
| 3 | Fiche lieu avec lecteur (`/lieu/{slug}`) | Titre, lecteur audio, début du texte de l'histoire (transcription) | Une histoire à écouter, un texte à lire | A story to hear, a text to read |
| 4 | Lecteur ouvert avec la mention IA | Barre de lecture, vitesse, « Voix générée par intelligence artificielle » visible | Voix de synthèse, sources citées | Synthetic voice, sources cited |
| 5 | Carte (`/carte`) | Pastilles colorées par catégorie, filtres (moins fréquentés, avec audio), **attribution « © OpenStreetMap contributors · Protomaps » visible** | Les lieux autour de vous | Places around you |
| 6 | Mode découverte actif | Bouton de mode découverte activé, histoire annoncée pour le lieu proche | L'histoire du lieu devant vous | The story of the place in front of you |
| 7 | Mes envies (`/envies`) + rappel de proximité | Liste groupée par destination, bandeau de rappel | Gardez vos envies, on vous prévient | Save places, get a heads-up |
| 8 | « Pour vous à Marseille » (`/destination`) | Liste ordonnée « Que visiter ? » | Préparez votre visite | Plan your visit |

Écrans à **ne pas** photographier : compte et connexion avec e-mail réel (`/compte`), tout écran d'erreur, tout écran qui laisserait voir une URL d'API ou un identifiant technique. Si l'écran Réglages vie privée (F-22) est livré avant la soumission, ajouter une 9e prise « Vos données vous appartiennent » (consentement statistiques, export, suppression) : c'est un argument de confiance fort.

## iPad et tablettes

Mêmes écrans 1, 3, 5, 6 en paysage et en portrait (la mise en page doit s'adapter : sinon ne pas soumettre de captures iPad déformées, corriger la mise en page d'abord). Si la prise en charge de l'iPad n'est pas voulue pour le MVP-0, retirer `UIDeviceFamily` 2 d'`Info.plist` plutôt que de fournir des captures médiocres.

## Visuel de présentation Google (1024 × 500)

Fond de la couleur de marque (`#0B6B7A`, celle de l'icône), nom « ON.VOYAGE » et la phrase « Le guide audio qui vous connaît », une capture de la carte à droite. Pas de capture d'écran de l'app dans le visuel si elle ne correspond pas au build publié.

## Vidéo d'aperçu (facultative)

15 à 30 secondes : onboarding → accueil → lecture d'une histoire écran verrouillé. Même contrainte : seulement des fonctions livrées ; la vidéo de **démonstration du service de premier plan** demandée par Google (voir `permissions.md`) est un document de revue distinct, non publié.
