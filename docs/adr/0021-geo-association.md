# ADR-0021 — Géo-association assistée par IA (T-1209)

- Statut : accepté — MVP. Hors périmètre : transcription des sous-titres et vision (V2, F-28), TikTok.

## Chaîne
`AnalyzeContentCommand` (message interne, après un import, un ajout de contenu par le créateur, ou « Analyser mes contenus ») → **lecture** (`IPlaceMentionExtractor`) → **chapitres** fusionnés → **candidats** du répertoire → **appariement** (`PlaceMatcher`, domaine pur) → **propositions** (`place_link` `proposed`) et **suggestions** de lieux inconnus (`PlaceSuggestedV1`). Tout se passe en arrière-plan (file locale de Wolverine) : aucun appel à un modèle ne se trouve sur le chemin d'une requête, aucune donnée de voyageur n'y entre, le texte lu est celui du créateur (titre, extrait de légende de 500 caractères, chapitres).

## Lecture
- Port `IPlaceMentionExtractor`, trois adaptateurs choisis par `Creators:GeoAssociation:Provider` : `disabled` (par défaut : rien n'est analysé, rien n'échoue, les contenus restent « pas analysés »), `offline` (déterministe, sans réseau, utilisé par tous les tests et par l'AppHost en local), `openai` (`IChatClient` de `Microsoft.Extensions.AI`, sortie structurée JSON Schema, température 0 ; démarrage refusé sans `OpenAI:ApiKey` et `Creators:Llm:GeotagModel`, le modèle restant à fixer, Q-14).
- Prompt versionné `prompts/geotag-places.md` (v1) : relever les lieux cités, ne rien inventer, une citation exacte par lieu, ne pas compléter un nom partiel (« Notre-Dame » reste « Notre-Dame »), un lieu par chapitre. **Garde-fou** : un lieu dont le nom n'apparaît pas dans le texte fourni est écarté (`PlaceMentionSource`), quoi que dise le modèle.
- Le lecteur hors ligne trouve des groupes de mots capitalisés (« Notre-Dame de la Garde », « Château d'If »), les listes sans séparateur (chaque mot est aussi essayé, **comme nom entier seulement**), le mot après une préposition de lieu et les hashtags (CamelCase séparé). Sa confiance est son estimation qu'une expression est un lieu (0,85 après « à », « vers »…, 0,6 pour un groupe de mots, 0,5 sinon) : elle ne sert qu'aux suggestions.
- Compteurs OpenTelemetry (`OnVoyage.Creators`) : `onvoyage.creators.geotag.contents|proposals|suggestions|reviews|failures`, `onvoyage.creators.llm.tokens`.

## Appariement
- Candidats : `poi_directory.search_text` (noms FR/EN et alias sans accents) par sous-chaîne (tirets et apostrophes lus comme des espaces) ou `pg_trgm` (`%>` et `word_similarity`), 12 au plus, par similarité décroissante.
- **Score** (0–1, déterministe) : similarité du nom avec chaque nom et alias (égalité 1 ; la mention est un morceau du nom 0,55–0,80 et « partielle » ; la mention contient le nom 0,7–0,95 ; sinon similarité trigramme comme `pg_trgm`, seuil 0,45), + ville concordante, − ville contradictoire (×0,7), + destinations du créateur (déclarées ou de ses lieux déjà validés), + chapitre explicite, léger bonus d'importance, − lieu non publié. **Désambiguïsation par les autres lieux du contenu** : les lieux de la destination majoritaire reçoivent un bonus, les autres un malus (le répertoire n'a pas de coordonnées : la destination tient lieu de proximité).
- **Plafonds** : un nom partiel n'atteint jamais 0,8 ; un rival à moins de 0,12 plafonne le meilleur à 0,85. Aucun des deux n'atteint 0,9 : ces propositions passent en revue manuelle (« Notre-Dame » sans contexte, critère d'acceptation F-28).
- Une proposition sous 0,4 n'est pas faite ; un lieu absent du catalogue, cité avec une certitude ≥ 0,7 par le lecteur (hors chapitre), devient `PlaceSuggestedV1` (une fois par contenu et par nom : `unmatched_mention`, unique) vers la file `factory`, avec la destination dominante du contenu.

## Validation
- **Aucune proposition n'est publiée** : `place_link.status = proposed` n'est lu par aucune requête publique, ne produit aucun événement (test d'intégration : page publique vide, rien dans la file de Discovery, aucune ligne `validated`). Seuls le créateur (Studio) ou un administrateur (écran existant) valident.
- Écran **« Propositions »** (`/studio/review`) : « Nous avons trouvé N lieux dans vos contenus », groupé par destination, confiance en %, étiquettes « à vérifier » et « nom ambigu », preuve et lien vers le contenu au chapitre ; **Valider / Refuser / Corriger** une par une ; **« Tout valider »** pour les propositions de 90 % ou plus (le service ne descend jamais sous son seuil, quelle que soit la requête). Une proposition refusée n'est plus jamais reproposée ; une correction refuse l'ancienne et valide le lieu choisi (signal `creator_correction`).
- Un créateur ne voit et ne décide que ses propositions ; un créateur suspendu ne peut pas décider.

## Écarts et limites
- **Qualité réelle non mesurée.** Le jeu de 50 légendes annotées (`GeoCorpus`) est écrit à la main avec le lecteur hors ligne en tête : à 90 % de confiance, 56 propositions, 56 justes ; 63 lieux attendus sur 63 trouvés, tous seuils confondus (65 propositions). Ce chiffre prouve la chaîne et le matcher, pas le modèle ; le critère « précision ≥ 0,9 au-dessus du seuil » devra être remesuré avec `openai` et de vraies légendes de créateurs (H-009).
- Seul l'**extrait de 500 caractères** de la légende est stocké, donc analysé ; une description longue de vidéo n'est lue que par ses chapitres (stockés à part) et son début.
- Pas de proximité géographique réelle : le répertoire de Creators n'a pas de coordonnées (règle de vie privée existante). La destination la remplace.
- Les écritures de validation sont journalisées chez Platform (acteur = le compte du créateur), comme les autres écritures du Studio (ADR-0019).

## Vérification
Domaine : 17 tests de l'appariement, 50 légendes annotées. Application : 14 tests du chaînage (jamais validé, une fois, désactivé, échec du modèle, plafonds, seuil de « Tout valider », cloisonnement). Intégration (PostgreSQL, lecteur hors ligne) : 9 tests (propositions groupées, chapitres horodatés, tout valider, valider/refuser/corriger, suggestion unique, cloisonnement, administrateur, analyse à la demande, export). Studio : 7 tests bUnit.
