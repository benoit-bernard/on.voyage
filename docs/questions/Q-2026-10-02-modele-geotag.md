# Q-2026-10-02 — Modèle de langage de la géo-association (Q-14)

- **Contexte** : T-1209 lit les lieux cités par les contenus des créateurs avec un modèle (`Creators:GeoAssociation:Provider = openai`, clé `Creators:Llm:GeotagModel`, cahier §27.2 Q-14 : « aucun défaut »). Aucun accès à OpenAI n'a existé pendant le développement : seul le lecteur hors ligne a été exercé.
- **Question** : quel modèle OpenAI pour `creators.geotag_model` (sortie structurée JSON Schema exigée, textes courts, volume = quelques centaines de contenus par créateur) ? Faut-il un tarif (`Factory:Llm:Pricing`-like) pour suivre le coût ?
- **À faire avant l'ouverture** : fixer le modèle, essayer le prompt `prompts/geotag-places.md` (v1) sur de vrais titres et légendes de créateurs fondateurs (H-009), et remesurer « précision ≥ 0,9 au-dessus du seuil » (le jeu de 50 légendes du dépôt a été écrit avec le lecteur hors ligne en tête : il valide la chaîne, pas le modèle).
- **Blocage** : aucun pour le développement ; le fournisseur reste `disabled` par défaut.
