# Étiquette de confidentialité Apple (« App Privacy ») — réponses

Brouillon du 2026-10-01. Sources : `docs/PRIVACY.md`, cahier des charges §16 (surtout §16.2 et §16.4), §17.3 et le schéma réel des services (`docs/DATABASE.md`). Ce document dit **quoi répondre** dans App Store Connect → App Privacy et ce qu'il faut mettre dans `PrivacyInfo.xcprivacy`.

Principe de rédaction : on déclare **au plus large** de ce que le registre autorise, jamais en dessous. Une déclaration trop prudente coûte un champ de plus ; une déclaration trop étroite coûte un rejet ou une correction publique.

## Réponses de haut niveau

| Question App Store Connect | Réponse | Pourquoi |
| --- | --- | --- |
| Collectez-vous des données de cette app ? | **Oui** | Compte facultatif (e-mail), identifiant voyageur, interactions, historique de lieux. |
| Des données servent-elles au **suivi** (tracking) ? | **Non** | D-02, §16.1 : aucune donnée à un tiers, aucun SDK publicitaire ou d'analyse tiers, pas d'identifiant publicitaire (IDFA). Pas d'invite App Tracking Transparency, pas de `NSUserTrackingUsageDescription`. |
| Des données sont-elles partagées avec des tiers à des fins publicitaires ? | **Non** | Pas de publicité en MVP-0 et MVP ; la régie V1.1 calculera le ciblage sur l'appareil (§16.1.3). |

## Types de données collectées

« Lié à l'utilisateur » = rattaché à l'identifiant voyageur (`traveler_id`, UUID aléatoire créé à la première ouverture) ou à l'e-mail. « État » dit si le flux existe déjà dans le code au 2026-10-01.

| Catégorie Apple | Type | Collecté | Lié | Suivi | Finalités à cocher | Source et justification | État |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Coordonnées | Adresse e-mail | Oui (**seulement** si l'utilisateur crée un compte) | Oui | Non | Fonctionnalités de l'app | PRIVACY.md : `platform.account`, `platform.otp_challenge` ; transite par Resend, sous-traitant (ADR-0005) | Implémenté |
| Identifiants | ID utilisateur | Oui | Oui | Non | Fonctionnalités de l'app ; Personnalisation | `platform.account.id`, `discovery.traveler.id`. Identifiant aléatoire, créé côté serveur, sans lien avec l'appareil | Implémenté |
| Données d'utilisation | Interaction avec le produit | Oui | Oui | Non | Fonctionnalités de l'app ; Personnalisation (+ **Analyse** quand les statistiques d'usage seront envoyées, voir ci-dessous) | §16.2 « Personnalisation » ; `discovery.interaction` (aime, bof, pas pour moi, enregistrer, écoute à 80 %, réécoute, abandon, ouverture de lien), `discovery.poi_rating`, `discovery.interest_vector`, `discovery.impression` | Implémenté (Discovery, ADR-0011) |
| Localisation | **Position approximative** | Oui, **sous forme d'historique de lieux** | Oui | Non | Fonctionnalités de l'app ; Personnalisation | §16.2 : l'historique des lieux (visites probables, écoutes horodatées) est une « donnée de localisation au sens du RGPD, même sans coordonnées » ; `discovery.visit` (lieu, jour, durée, confiance) | Implémenté côté serveur ; envoi par l'app via `POST /me/interactions` |
| Contenu utilisateur | Autre contenu d'utilisateur | Oui (signalement d'une histoire : motif ≤ 500 caractères) | Oui (identifiant voyageur) | Non | Fonctionnalités de l'app | PRIVACY.md « Factory » : signalement | Route serveur prête (`POST /api/factory/v1/stories/{id}/reports`), écran de l'app **non encore présent** : ne pas déclarer tant qu'aucun build soumis ne l'envoie |
| Diagnostics | Données de plantage ; Performances ; Autres données de diagnostic | Oui (**événements techniques essentiels** : `app_crash`, `audio_error`, `gps_loss`, avec version, plateforme, code d'erreur) | **Non** | Non | Fonctionnalités de l'app | §16.2 « Événements techniques essentiels » (intérêt légitime, 90 jours), §17.3 : aucune propriété d'identité | **Non implémenté** (service Insights, T-801) : déclarer au moment où le build les envoie |

### Données que l'app ne collecte pas (à laisser « Non collecté »)

| Catégorie | Pourquoi |
| --- | --- |
| **Position précise** | Les coordonnées ne servent qu'à la requête en cours : `lat` et `lon` arrondis à 3 décimales (~100 m) dans `GET /api/catalog/v1/destinations/{slug}/pois`, jamais stockés ni journalisés (PRIVACY.md ; l'instrumentation expurge `url.query` et `url.full`, Caddy supprime la requête de ses journaux). Selon la définition d'Apple, une donnée traitée pour répondre en temps réel et non conservée n'est **pas** « collectée ». Le déclenchement, les rappels et les visites sont calculés sur l'appareil. |
| Identifiants > ID de l'appareil, IDFA | Aucun identifiant matériel ou publicitaire (§16.1.2). |
| Achats, Informations financières | Aucun achat en MVP-0 (Billing arrive au MVP ; les achats sont gérés par les stores). À déclarer « Historique des achats » avec Billing. |
| Historique de recherche, de navigation | Pas de recherche en MVP-0 ; au MVP le texte saisi n'est jamais lu ni envoyé (§17.3). |
| Santé et forme, Informations sensibles, Contacts, Photos ou vidéos, Audio (enregistrements), Messages, Environnement, Corps | Jamais demandés ; aucune permission correspondante. |
| Nom, numéro de téléphone, adresse postale | Aucun nom ; le compte n'a qu'un e-mail et aucun mot de passe. |

Note sur le statut « position approximative » : si le propriétaire estime que l'historique de lieux (un lieu nommé, sans coordonnées) n'est pas une donnée de localisation au sens d'Apple, la ligne Localisation peut être retirée ; le registre §16.2 dit explicitement le contraire, c'est donc la réponse prudente retenue ici.

## Statistiques d'usage (consentement)

Les événements du catalogue §17.3 (ouverture de l'app, écoutes, clics) ne partent qu'après le **consentement** « Statistiques d'usage » (opt-in, absence de choix = refus, §16.3). Au 2026-10-01 aucun écran de consentement et aucun envoi n'existent (`HoldingSyncTransport` garde les événements en file ; Insights n'est pas construit). Quand ils seront livrés :

- cocher la finalité **Analyse** sur « Interaction avec le produit » ;
- les identifiants des événements restent l'identifiant voyageur : « Lié à l'utilisateur » reste **Oui** ;
- Apple n'a pas de case « facultatif » pour cela : la déclaration vaut pour tous les utilisateurs même si l'envoi dépend du consentement.

## Fichier `PrivacyInfo.xcprivacy` (à ajouter dans `src/Mobile/OnVoyage.App/Platforms/iOS/`)

À la date de ce document, le dépôt ne contient pas de manifeste de confidentialité. Contenu attendu, cohérent avec les réponses ci-dessus :

| Clé | Valeur |
| --- | --- |
| `NSPrivacyTracking` | `false` |
| `NSPrivacyTrackingDomains` | liste vide |
| `NSPrivacyCollectedDataTypes` | une entrée par ligne du tableau « collectées » (e-mail ; ID utilisateur ; interaction avec le produit ; position approximative ; autre contenu d'utilisateur quand il sera envoyé), chacune avec `NSPrivacyCollectedDataTypeLinked` = `true`, `…Tracking` = `false` et les finalités ci-dessus |
| `NSPrivacyAccessedAPITypes` | à établir par analyse du binaire MAUI (au moins `UserDefaults` / préférences : raison `CA92.1`) ; à vérifier avec l'avertissement d'App Store Connect au premier envoi |

## Politique de confidentialité et éléments liés

- URL de la politique : voir `listing-fr.md` (à créer ; reprendre le registre `docs/PRIVACY.md`, les durées de conservation §16.2, les sous-traitants §16.4, les droits §16.5 et les coordonnées de la CNIL).
- **Suppression de compte** : l'App Store (guideline 5.1.1(v)) exige une suppression **dans l'app** dès qu'un compte peut être créé dans l'app. F-22 / T-507 n'est pas implémenté : **bloquant pour la soumission publique** (pas pour TestFlight interne).
- Chiffrement : `ITSAppUsesNonExemptEncryption` = `false` est déjà dans `Info.plist` (HTTPS standard uniquement).
