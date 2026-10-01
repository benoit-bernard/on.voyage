# Justification des permissions

Brouillon du 2026-10-01. Les permissions listées sont celles **réellement déclarées** dans `src/Mobile/OnVoyage.App/Platforms/Android/AndroidManifest.xml` et `Platforms/iOS/Info.plist`. Les textes d'invite système de référence sont ceux de l'annexe G du cahier des charges.

## Permissions déclarées en MVP-0

### Localisation, pendant l'utilisation

| Plateforme | Déclaration | Quand l'invite apparaît |
| --- | --- | --- |
| iOS | `NSLocationWhenInUseUsageDescription` | seulement quand l'utilisateur active le mode découverte ou ouvre la carte « autour de moi » (ADR-0010) |
| Android | `ACCESS_FINE_LOCATION`, `ACCESS_COARSE_LOCATION` (+ `uses-feature android.hardware.location.gps` avec `required="false"`) | idem ; l'utilisateur peut n'accorder que la position approximative |

**Justification pour la revue** (App Review et Play Console) :

> ON.VOYAGE est un guide audio : il raconte l'histoire du lieu devant lequel l'utilisateur se trouve. La position est lue uniquement quand l'app est ouverte et que l'utilisateur a demandé la fonction (carte « autour de moi », mode découverte). Elle sert à trouver les lieux proches et à décider quelle histoire proposer. Le déclenchement automatique, les rappels de proximité et les visites sont calculés sur l'appareil. Les coordonnées, arrondies à 3 décimales, ne sont envoyées que dans la requête qui liste les lieux proches ; elles ne sont ni enregistrées ni journalisées sur nos serveurs. Sans la permission, l'app reste utilisable : accueil « Pour vous », fiches, lecture audio et envies fonctionnent ; seules les fonctions « autour de moi » sont indisponibles.

**Écart à corriger avant la soumission** : le texte actuel de `NSLocationWhenInUseUsageDescription` est « ON.VOYAGE utilise votre position, sur l'appareil uniquement, pour proposer des lieux autour de vous. » Or l'app envoie les coordonnées arrondies dans `GET …/pois` (`HttpCatalogClient`), donc « sur l'appareil uniquement » est inexact et un relecteur peut le relever. Texte de l'annexe G à utiliser : « ON.VOYAGE utilise votre position pour vous montrer les lieux autour de vous et vous raconter leur histoire. Vos coordonnées GPS ne sont jamais conservées sur nos serveurs. »

### Audio en arrière-plan

| Plateforme | Déclaration |
| --- | --- |
| iOS | `UIBackgroundModes` = `audio` |
| Android | `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_MEDIA_PLAYBACK`, `WAKE_LOCK` ; service `communitytoolkit.maui.media.services.MediaControlsService` avec `foregroundServiceType="mediaPlayback"` |

**Justification** :

> Le produit est une lecture audio. L'utilisateur lance une histoire de quelques minutes et poursuit sa marche, téléphone en poche ou écran verrouillé. Le mode d'arrière-plan `audio` (iOS) et le service de premier plan de type `mediaPlayback` (Android) servent uniquement à poursuivre cette lecture et à afficher les commandes de l'écran verrouillé (pause, reprise, retour de 10 s). Ils ne servent ni à la localisation ni à aucun autre traitement en arrière-plan. La lecture s'arrête avec la tâche (`android:stopWithTask="true"`) et la notification de lecture est celle, standard, du lecteur multimédia.

Google Play : renseigner la déclaration **Services de premier plan → Lecture de médias** (description ci-dessus + vidéo courte : lancer une histoire, verrouiller l'écran, utiliser les commandes, arrêter).

### Réseau

`INTERNET` et `ACCESS_NETWORK_STATE` (Android) : accès au Gateway de l'API et au CDN. Aucune justification particulière demandée. Aucune requête vers un tiers autre que les liens sortants ouverts par l'utilisateur (Wikipédia, vidéos) dans le navigateur ou l'app YouTube.

## Notifications

**Aucune permission de notification n'est déclarée ni demandée en MVP-0** : le manifeste Android ne contient pas `POST_NOTIFICATIONS` et l'app n'appelle pas `UNUserNotificationCenter`. Les rappels de proximité (F-08) s'affichent dans l'app au premier plan (`ReminderBanner`), et le récapitulatif de trajet prévoit une notification locale « si l'hôte en fournit » (ADR-0011), ce que l'hôte MAUI ne fait pas aujourd'hui.

Texte préparé pour le jour où elles seront ajoutées (MVP, avec le mode découverte en arrière-plan) :

| Plateforme | Quand | Invite | Justification |
| --- | --- | --- | --- |
| Android 13+ | à l'activation des rappels ou du mode découverte en arrière-plan, jamais au premier lancement | `POST_NOTIFICATIONS` | « Afficher la notification persistante obligatoire du mode découverte (« ON.VOYAGE vous accompagne — Arrêter ») et les rappels de proximité des lieux que l'utilisateur a enregistrés. Aucune notification promotionnelle. » |
| iOS | à l'activation des rappels | alerte de notification locale | « Prévenir l'utilisateur, à sa demande, qu'il passe près d'un lieu qu'il a enregistré. Notifications **locales** uniquement : aucun serveur de notifications push, aucun jeton envoyé à un tiers. » |

Règle produit à garder : notifications locales seulement, ni push ni marketing (cohérent avec §16 : aucun SDK tiers).

## Non demandées en MVP-0 (à préparer pour le MVP)

| Permission | Pourquoi pas maintenant | À préparer |
| --- | --- | --- |
| Localisation en arrière-plan (`ACCESS_BACKGROUND_LOCATION`, iOS « Toujours ») | Mode découverte au premier plan, écran actif, en MVP-0 (matrice §3.2) ; arrière-plan = T-612 | Textes de l'annexe G (`NSLocationAlwaysAndWhenInUseUsageDescription`, écran d'explication avant la demande Android). Google exige un formulaire de déclaration, une vidéo et une revue : prévoir plusieurs jours. Apple examine l'usage « Toujours » avec attention. |
| Caméra, micro, contacts, photos, calendrier, Bluetooth | Aucune fonction | — |
| Suivi inter-apps (ATT) | Aucun suivi | — |
