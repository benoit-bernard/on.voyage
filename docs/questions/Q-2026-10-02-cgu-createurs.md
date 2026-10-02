# Q-2026-10-02 — Texte des CGU créateurs (H-010)

- **Contexte** : T-1206 (`Web.Studio`) demande au créateur d'accepter les CGU créateurs avant de publier (F-26). Le texte juridique est la tâche humaine H-010 (un juriste : licence d'affichage des vignettes et textes, transparence commerciale, modération, fin de relation).
- **Fait dans la tranche** : le serveur porte la **version** à accepter (`Creators:Terms:CurrentVersion`, défaut `2026-10`) ; l'espace affiche une synthèse provisoire des engagements du cahier (F-26, F-27, F-33) marquée « Texte provisoire » (`src/Web/OnVoyage.Web.Studio/Text/CreatorTerms.cs`).
- **Question** : quand le texte de H-010 est prêt, où le publier (page `Web.Public`, PDF, texte dans le Studio) et quelle version lui donner ? Le changement de `Creators:Terms:CurrentVersion` suffit à redemander l'acceptation à tous les créateurs (`POST /studio/terms`).
- **Blocage** : aucun pour le développement ; **bloquant pour l'ouverture publique** de l'inscription libre.
