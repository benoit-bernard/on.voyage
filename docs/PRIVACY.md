# Registre des traitements — ON.VOYAGE (MVP-0, tranche actuelle)

Source : cahier des charges §16. Ce fichier est mis à jour dans la même PR que toute nouvelle donnée personnelle stockée.

| Donnée | Où | Finalité | Sortie de l'appareil ? |
| --- | --- | --- | --- |
| Profil de goûts (affinités par catégorie, profondeur, envies, identifiant local) | Appareil : préférences MAUI (app) ou `localStorage` (PWA) | Classement « Pour vous » calculé sur l'appareil | **Non** |
| Position GPS (premier plan) | Mémoire, le temps de l'appel | Distance aux lieux | Uniquement en paramètres `lat`/`lon` (3 décimales) de `GET …/pois` ; jamais stockée ni journalisée |
| Consentements (`analytics`, `ads_personalization`) : identifiant voyageur, genre, accordé, version du texte, date | Serveur : `platform.consent` | Respect des choix (§16.3) ; opt-in, aucun enregistrement = refusé | Non ; l'événement `ConsentChangedV1` ne circule qu'entre services internes |
| Compte : identifiant voyageur, e-mail vérifié (si l'utilisateur crée un compte), rôles, dates de création et de dernière activité | Serveur : `platform.account` | Authentification (F-01) ; aucun nom, aucun mot de passe | Non |
| Code de connexion : empreinte HMAC du code, adresse, expiration, essais | Serveur : `platform.otp_challenge` | Vérifier l'e-mail ; seule l'empreinte est stockée | Non |
| Jeton de rafraîchissement : empreinte, compte, expiration | Serveur : `platform.refresh_token` | Maintenir la session | Non |
| **Adresse e-mail + code à 6 chiffres** | Transitent par **Resend** (sous-traitant, ADR-0005) | Envoi du code de connexion | **Oui**, à Resend uniquement (DPA et région UE à finaliser, voir `docs/questions/Q-2026-10-01-resend.md`) |
| Jetons de session (accès + rafraîchissement) | Appareil : `SecureStorage` (app) ou `localStorage` (PWA) | Rester connecté | Non |

| **Événements d'usage** (catalogue §17.3 : ouverture, écrans vus, écoutes, retours, etc. ; propriétés listées, jamais de position, de texte saisi ni d'identifiant d'appareil) : identifiant voyageur, identifiant de session aléatoire, version d'app, plateforme, date | Serveur : `insights.event` (partitions mensuelles) | Statistiques d'usage (§26) ; **uniquement si le voyageur a accepté** (consentement `analytics` ; sans ligne dans `insights.consent_projection` = refusé) | Non ; restent dans Insights |
| **Événements techniques essentiels** (`app_crash`, `audio_error`, `gps_loss` : code d'erreur, version, plateforme) : mêmes champs | Serveur : `insights.event` | Sécurité et fonctionnement (intérêt légitime), envoyés même sans consentement | Non ; **90 jours** |
| Projection du consentement statistiques : identifiant voyageur, accordé ou non, date | Serveur : `insights.consent_projection` | Filtrer les événements à la réception | Non |
| Agrégats quotidiens (comptes par jour, cohorte et indicateur) | Serveur : `insights.daily_kpi` | Indicateurs produit (§26) | Non ; **anonymes, conservés** |
| File d'événements en attente d'envoi (appareil hors ligne) | Appareil : mémoire de l'app, puis `user.db` quand l'hôte la fournit | Envoi différé des événements autorisés | Non tant qu'elle n'est pas envoyée ; les événements non essentiels sont supprimés si le voyageur refuse |

Conservation des événements d'usage : **13 mois** bruts (`retention.analytics_raw_months`), suppression par partition mensuelle puis découpage du mois le plus ancien ; événements essentiels : 90 jours (`retention.technical_events_days`). Suppression de compte : tous les événements et la projection du voyageur sont supprimés (`TravelerDeletionRequestedV1`) ; export : un fichier JSON des événements du voyageur (`TravelerExportRequestedV1`). Retirer le consentement n'efface pas les événements déjà reçus (ils partent à la suppression du compte ou au bout de 13 mois).

Mesures techniques en place : l'instrumentation OpenTelemetry expurge `url.query` et `url.full` (`ServiceDefaults`) ; aucun SDK d'analytics, de crash, de publicité ou de paiement tiers (test d'architecture `No_third_party_analytics_crash_ads_or_payment_sdk`) ; aucune ressource tierce dans les pages du client.

Non encore implémenté : orchestration de l'export et de la suppression côté Platform, écran des réglages de confidentialité (F-01, F-22, T-507, T-618). Les gestionnaires d'Insights existent (ADR-0012).

## Factory et fournisseurs de modèles
Les textes envoyés à OpenAI (extraction, rédaction, vérification, voix) sont des données publiques (articles Wikipédia, faits dérivés). Aucune donnée de voyageur, position ou identifiant n'y est jointe. Le signalement d'une histoire ne stocke que l'identifiant du voyageur et le motif (500 caractères), pour limiter les abus.

## Liens vers YouTube et Wikipédia
La fiche d'un lieu propose des liens sortants (Wikipédia, deux vidéos au plus). Ce sont de simples liens : aucun lecteur intégré, aucun script ni image de YouTube dans l'app (la vignette est une copie hébergée chez nous). YouTube ne reçoit rien tant que le voyageur n'ouvre pas le lien ; la recherche et le choix des vidéos se font côté serveur, par un administrateur.
