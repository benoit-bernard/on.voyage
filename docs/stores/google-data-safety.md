# Formulaire « Sécurité des données » Google Play (Data safety) — réponses

Brouillon du 2026-10-01. Mêmes sources et même règle que l'étiquette Apple (`apple-privacy-label.md`) : `docs/PRIVACY.md`, §16 et §17.3, schéma réel des services. À saisir dans Play Console → Contenu de l'application → Sécurité des données. Les intitulés suivent la console en français ; l'anglais d'origine est entre parenthèses quand il aide.

## Questions préalables

| Question | Réponse | Justification |
| --- | --- | --- |
| L'app collecte-t-elle ou partage-t-elle des types de données utilisateur requis ? | **Oui** | Voir le tableau suivant. |
| Toutes les données collectées sont-elles chiffrées en transit ? | **Oui** | HTTPS partout, TLS 1.2+ (SEC-01) ; HSTS côté Caddy. |
| Proposez-vous aux utilisateurs un moyen de demander la suppression de leurs données ? | **Non tant que F-22 / T-507 n'est pas livré, puis Oui** | Règle Play : une app qui permet de créer un compte **dans l'app** doit proposer la suppression du compte dans l'app **et** une URL web. L'export et la suppression ne sont pas implémentés (PRIVACY.md « Non encore implémenté »). **Bloquant pour la publication en production**, pas pour une piste de test interne. |
| L'app respecte-t-elle la politique Familles (public enfants) ? | **Non applicable** | L'app ne cible pas les enfants (le « mode enfants » est V2). Public cible : 13 ans et plus [à confirmer avec la déclaration « Public cible »]. |
| Un examen de sécurité indépendant a-t-il été fait (facultatif) ? | Non pour l'instant | La revue OWASP ASVS niveau 1 est T-1102. |
| Autres déclarations liées | Pas de publicité (MVP-0 et MVP) ; accès à l'app : aucun identifiant requis | Voir `review-notes.md`. |

## Types de données

Règles de Google appliquées :

- **« Partagé »** ne comprend pas le transfert à un prestataire qui traite pour notre compte (Resend pour l'envoi du code, hébergeur UE) : aucune donnée n'est donc « partagée ».
- **« Traitement éphémère »** : une donnée lue en mémoire pour répondre à la requête et non conservée n'a pas à être déclarée comme collectée. C'est le cas des coordonnées GPS.

| Catégorie | Type de données | Collectée | Partagée | Éphémère | Obligatoire ou facultative | Finalités | Source |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Infos personnelles | Adresse e-mail | Oui | Non | Non | **Facultative** (l'app marche en session anonyme) | Gestion du compte ; Fonctionnement de l'app | PRIVACY.md `platform.account`, `otp_challenge` |
| Infos personnelles | ID utilisateur | Oui | Non | Non | Obligatoire | Fonctionnement de l'app ; Personnalisation ; Gestion du compte | `platform.account.id`, `discovery.traveler.id` |
| Activité dans l'application | Interactions avec l'app | Oui | Non | Non | Obligatoire (c'est le service de personnalisation) | Fonctionnement de l'app ; Personnalisation (+ **Analyse**, **facultatif**, quand les statistiques d'usage seront envoyées après consentement) | §16.2 ; `discovery.interaction`, `poi_rating`, `interest_vector`, `impression` |
| Position | **Position approximative** | Oui, **sous forme d'historique de lieux** (lieux écoutés ou visités, avec la date) | Non | Non | Obligatoire pour la personnalisation | Fonctionnement de l'app ; Personnalisation | §16.2 « Historique des lieux » ; `discovery.visit` |
| Activité dans l'application | Autre contenu généré par l'utilisateur | Oui (motif d'un signalement, 500 caractères) | Non | Non | Facultative | Fonctionnement de l'app | PRIVACY.md « Factory » — **à déclarer seulement quand l'écran de signalement sera livré** |
| Infos sur l'application et performances | Journaux de plantage ; Diagnostics | Oui (événements techniques essentiels, **sans identifiant voyageur**) | Non | Non | Obligatoire | Fonctionnement de l'app | §16.2, §17.3 — **non implémenté** (Insights, T-801) : déclarer au moment où le build les envoie |

### Types de données **non** collectés

| Type | Pourquoi |
| --- | --- |
| **Position précise** | Éphémère : `lat` et `lon` (3 décimales) ne sont lus que pour répondre à `GET …/pois`, jamais stockés ni journalisés (PRIVACY.md). Si le propriétaire préfère la prudence maximale, déclarer « Position précise : collectée, non partagée, **traitée de manière éphémère**, obligatoire, fonctionnement de l'app » : c'est une réponse valide aussi, sans conséquence sur le reste. |
| Identifiants de l'appareil ou autres | Aucun identifiant publicitaire, aucun identifiant matériel (§16.1.2). |
| Infos financières, historique des achats | Aucun achat en MVP-0. À déclarer avec Billing (MVP). |
| Santé, messages, photos, fichiers, audio enregistré, agenda, contacts | Jamais demandés, aucune permission. |
| Historique de navigation web, de recherche | Pas de recherche en MVP-0 ; au MVP, le texte saisi n'est jamais lu (§17.3). |

## Sécurité et gestion

| Point | Réponse |
| --- | --- |
| Chiffrement en transit | Oui |
| Chiffrement au repos | Déclaration non demandée par le formulaire ; `allowBackup="false"` dans le manifeste (pas de sauvegarde Android automatique des données locales) ; les jetons sont dans `SecureStorage` |
| Possibilité de demander la suppression | voir plus haut (bloquant) |
| Partage de données avec des tiers | Aucun (D-02) |
| Durées de conservation à annoncer dans la politique | Celles de §16.2 : compte = durée du compte (anonymes inactifs purgés après 24 mois, **purge non encore implémentée**), historique de lieux = durée du compte, essentiels = 90 jours, journaux serveur = 30 jours |

## Déclarations Play Console voisines

| Rubrique | Réponse |
| --- | --- |
| Publicités | « Non, l'app ne contient pas de publicité » (MVP-0, MVP). À changer avec la régie V1.1. |
| Accès à l'application | Toutes les fonctions accessibles sans compte ni identifiant (voir `review-notes.md`). |
| Autorisations de localisation | Premier plan uniquement (`ACCESS_FINE_LOCATION`, `ACCESS_COARSE_LOCATION`) ; pas de formulaire « localisation en arrière-plan » en MVP-0 (voir `permissions.md`). |
| Services de premier plan | Déclarer le type `mediaPlayback` et sa justification (voir `permissions.md`) : Google demande une description et une vidéo de démonstration. |
| Classification du contenu | Voir `age-rating.md`. |
| Applications d'actualités, santé, finances, gouvernement | Non applicable. |
