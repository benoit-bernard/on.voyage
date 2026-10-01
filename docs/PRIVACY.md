# Registre des traitements — ON.VOYAGE (MVP-0, tranche actuelle)

Source : cahier des charges §16. Ce fichier est mis à jour dans la même PR que toute nouvelle donnée personnelle stockée.

| Donnée | Où | Finalité | Sortie de l'appareil ? |
| --- | --- | --- | --- |
| Profil de goûts local (affinités par catégorie, profondeur, envies, lieux écartés, identifiant) | Appareil : préférences MAUI (app) ou `localStorage` (PWA) | Classement « Pour vous » et « Que visiter ? » calculés sur l'appareil, réponse immédiate | Copie du vecteur serveur : voir la ligne suivante |
| **Interactions** : retours (j'aime, bof, pas pour moi), enregistrements, écoute à 80 %, réécoute, abandon, visites probables (lieu, durée, confiance), réponses d'onboarding ; identifiant d'événement, lieu, date | Serveur : `discovery.interaction`, `discovery.visit`, `discovery.impression`, `discovery.saved_poi` ; file d'envoi `user.db` sur l'appareil | Personnalisation (service demandé) : calcul du vecteur d'intérêts, exclusion des lieux écartés, historique consultable (F-22) | **Oui**, vers Discovery (UE) ; indépendant du consentement statistiques. **Aucune coordonnée** : un lieu et une durée, jamais un trajet (test sur le schéma) |
| **Vecteur d'intérêts** (74 valeurs), verrous, profondeur, cohorte, langue, mode éthique | Serveur : `discovery.interest_vector`, `discovery.traveler` | Recommandations (§6) ; recalculé en rejouant l'historique | Oui, vers Discovery ; modifiable et réinitialisable par l'utilisateur, supprimé avec le compte |
| Dates de rappel de proximité (`last_reminded_at`) | Appareil : `user.db` / `localStorage` | Limiter les rappels (F-08) | **Non, jamais synchronisé** (il révélerait un passage à proximité) |
| Position GPS (premier plan, mode découverte) | Mémoire, le temps du calcul | Distance aux lieux, déclenchement des histoires, rappels de proximité | Uniquement en paramètres `lat`/`lon` (3 décimales) de `GET …/pois` et des recommandations ; jamais stockée ni journalisée ; le mode découverte décide sur l'appareil |
| Consentements (`analytics`, `ads_personalization`) : identifiant voyageur, genre, accordé, version du texte, date | Serveur : `platform.consent` | Respect des choix (§16.3) ; opt-in, aucun enregistrement = refusé | Non ; l'événement `ConsentChangedV1` ne circule qu'entre services internes |
| Compte : identifiant voyageur, e-mail vérifié (si l'utilisateur crée un compte), rôles, dates de création et de dernière activité | Serveur : `platform.account` | Authentification (F-01) ; aucun nom, aucun mot de passe | Non |
| Code de connexion : empreinte HMAC du code, adresse, expiration, essais | Serveur : `platform.otp_challenge` | Vérifier l'e-mail ; seule l'empreinte est stockée | Non |
| Jeton de rafraîchissement : empreinte, compte, expiration | Serveur : `platform.refresh_token` | Maintenir la session | Non |
| **Adresse e-mail + code à 6 chiffres** | Transitent par **Resend** (sous-traitant, ADR-0005) | Envoi du code de connexion | **Oui**, à Resend uniquement (DPA et région UE à finaliser, voir `docs/questions/Q-2026-10-01-resend.md`) |
| Jetons de session (accès + rafraîchissement) | Appareil : `SecureStorage` (app) ou `localStorage` (PWA) | Rester connecté | Non |

Mesures techniques en place : l'instrumentation OpenTelemetry expurge `url.query` et `url.full` (`ServiceDefaults`) ; aucun SDK d'analytics, de crash, de publicité ou de paiement tiers (test d'architecture `No_third_party_analytics_crash_ads_or_payment_sdk`) ; aucune ressource tierce dans les pages du client.

Mise à jour en cours : événements d'usage (Insights, T-801), export et suppression des données (T-507) — voir les lignes correspondantes une fois livrés.

## Factory et fournisseurs de modèles
Les textes envoyés à OpenAI (extraction, rédaction, vérification, voix) sont des données publiques (articles Wikipédia, faits dérivés). Aucune donnée de voyageur, position ou identifiant n'y est jointe. Le signalement d'une histoire ne stocke que l'identifiant du voyageur et le motif (500 caractères), pour limiter les abus.

## Liens vers YouTube et Wikipédia
La fiche d'un lieu propose des liens sortants (Wikipédia, deux vidéos au plus). Ce sont de simples liens : aucun lecteur intégré, aucun script ni image de YouTube dans l'app (la vignette est une copie hébergée chez nous). YouTube ne reçoit rien tant que le voyageur n'ouvre pas le lien ; la recherche et le choix des vidéos se font côté serveur, par un administrateur.
