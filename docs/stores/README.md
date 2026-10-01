# Fiches stores — brouillons (T-1103)

Brouillons du 2026-10-01 pour App Store Connect et Google Play Console. Tout ce qui touche aux données personnelles est **dérivé de `docs/PRIVACY.md` et du cahier des charges §16**, puis recoupé avec le code (schémas `docs/DATABASE.md`, manifestes de `src/Mobile/OnVoyage.App`) ; ce n'est pas du texte marketing. Les points entre crochets `[…]` sont à trancher par le propriétaire.

| Fichier | Contenu |
| --- | --- |
| [listing-fr.md](listing-fr.md), [listing-en.md](listing-en.md) | nom, sous-titre, texte promotionnel, descriptions, mots-clés, catégories, URL d'assistance et de confidentialité (espaces réservés), notes de version |
| [check-lengths.py](check-lengths.py) | vérifie les longueurs des champs (`python3 docs/stores/check-lengths.py`) |
| [apple-privacy-label.md](apple-privacy-label.md) | réponses de l'étiquette « App Privacy » et du `PrivacyInfo.xcprivacy` |
| [google-data-safety.md](google-data-safety.md) | réponses du formulaire « Sécurité des données » |
| [permissions.md](permissions.md) | justification de la localisation, de l'audio en arrière-plan, des notifications |
| [age-rating.md](age-rating.md) | classification d'âge, catégories, déclarations (IA, chiffrement, DSA) |
| [screenshots.md](screenshots.md) | formats et liste de prises de vue |
| [review-notes.md](review-notes.md) | notes pour les relecteurs |

## Résumé des déclarations de confidentialité

| Donnée | Apple | Google | Condition |
| --- | --- | --- | --- |
| E-mail (compte facultatif) | collecté, lié, fonctionnalités | collecté, facultatif | — |
| Identifiant voyageur (UUID) | collecté, lié | collecté | — |
| Interactions (aime, écoute, enregistre…) | collecté, lié, fonctionnalités et personnalisation | collecté | + Analyse après consentement, quand les statistiques existeront |
| Historique de lieux (visites, écoutes) | position approximative, liée | position approximative | §16.2 le qualifie de donnée de localisation |
| Position précise (GPS) | **non collectée** | **non collectée** (éphémère) | paramètres `lat`/`lon` de la requête des lieux proches, arrondis, jamais stockés |
| Signalement d'une histoire | autre contenu d'utilisateur | contenu généré | quand l'écran existera |
| Plantages, erreurs techniques | diagnostics, non lié | journaux de plantage | quand Insights existera |
| Suivi, publicité, SDK tiers | aucun | aucun | test d'architecture `No_third_party_analytics_crash_ads_or_payment_sdk` |

## Écarts relevés en préparant ces documents

À traiter hors de cette tranche (aucun fichier de `src/` n'a été modifié) :

1. **Texte de la permission de localisation iOS inexact** : `Info.plist` dit « sur l'appareil uniquement » alors que les coordonnées arrondies partent dans `GET …/pois`. Utiliser le texte de l'annexe G (détail dans `permissions.md`).
2. **`docs/PRIVACY.md` en retard sur le code** : son registre ne mentionne pas les données du service Discovery (`discovery.traveler`, `interest_vector`, `interaction`, `poi_rating`, `visit`, `impression`, ADR-0011) et affirme que le profil de goûts ne quitte pas l'appareil, alors que le serveur le recalcule et le garde. La règle du dépôt exige de les ajouter dans la même PR que le code. Les réponses des stores ci-dessus supposent le registre corrigé (§16.2 les couvre déjà).
3. **Pas de suppression ni d'export de compte** (F-22, T-507) : exigence Apple 5.1.1(v) et Google Play dès qu'un compte se crée dans l'app. Bloquant pour toute publication publique.
4. **Pas de consentement « Statistiques d'usage »** dans l'app, pas d'envoi d'événements d'usage, pas d'écran de signalement : les lignes concernées des formulaires ne sont à déclarer que lorsque ces fonctions sont dans le build soumis.
5. **Pas de `PrivacyInfo.xcprivacy`** dans le projet iOS (voir `apple-privacy-label.md`).
6. **Pages légales absentes** : les URL d'assistance et de confidentialité sont des espaces réservés (le site public SEO, F-24, n'existe pas encore).

## Avant la première soumission publique

- [ ] Tous les points de la liste « Écarts » ci-dessus sont fermés.
- [ ] Les champs `[…]` des fiches sont remplis par le propriétaire (raison sociale, contacts, statut DSA).
- [ ] Les réponses des formulaires sont relues **contre le build soumis** (une fonction non livrée ne se déclare pas, une fonction livrée se déclare).
- [ ] Captures prises sur le build soumis (`screenshots.md`).
- [ ] Vidéo de démonstration du service de premier plan (Google).
- [ ] Revue sécurité T-1102 terminée (SEC-13).
