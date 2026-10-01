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

Mesures techniques en place : l'instrumentation OpenTelemetry expurge `url.query` et `url.full` (`ServiceDefaults`) ; aucun SDK d'analytics, de crash, de publicité ou de paiement tiers (test d'architecture `No_third_party_analytics_crash_ads_or_payment_sdk`) ; aucune ressource tierce dans les pages du client.

Non encore implémenté : événements d'usage, export et suppression (F-01, F-22, Insights, Platform).
