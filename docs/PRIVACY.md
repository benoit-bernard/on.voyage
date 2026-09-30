# Registre des traitements — ON.VOYAGE (MVP-0, tranche actuelle)

Source : cahier des charges §16. Ce fichier est mis à jour dans la même PR que toute nouvelle donnée personnelle stockée.

| Donnée | Où | Finalité | Sortie de l'appareil ? |
| --- | --- | --- | --- |
| Profil de goûts (affinités par catégorie, profondeur, envies, identifiant local) | Appareil : préférences MAUI (app) ou `localStorage` (PWA) | Classement « Pour vous » calculé sur l'appareil | **Non** |
| Position GPS (premier plan) | Mémoire, le temps de l'appel | Distance aux lieux | Uniquement en paramètres `lat`/`lon` (3 décimales) de `GET …/pois` ; jamais stockée ni journalisée |

Mesures techniques en place : l'instrumentation OpenTelemetry expurge `url.query` et `url.full` (`ServiceDefaults`) ; aucun SDK d'analytics, de crash, de publicité ou de paiement tiers (test d'architecture `No_third_party_analytics_crash_ads_or_payment_sdk`) ; aucune ressource tierce dans les pages du client.

Non encore implémenté : session anonyme Supabase, OTP, événements d'usage, export et suppression (F-01, F-22, Insights, Platform).
