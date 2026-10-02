# ADR-0017 — Snapshot de contenu, fournisseurs hors ligne et amorçage d'une destination (Marseille)

- Statut : accepté. Complète les ADR-0007 et 0008. Objectif : un catalogue de Marseille (lieux, histoires, audio) qui se génère par le pipeline Factory, se **met en cache** et se lance en local, avec ou sans clés.

## Deux modes, mêmes ports
1. **En ligne (réel)** : `bootstrap` enchaîne import OSM → enrichissement Wikidata → scoring → sources Wikipédia → faits → rédaction (modèle) → contrôles → approbation → voix → publication. Procédure et plafond de coût : [runbooks/bootstrap-marseille.md](../runbooks/bootstrap-marseille.md).
2. **Hors ligne (démo, tests)** : `Factory:Llm:Provider=offline` remplace le modèle de langage par des adaptateurs déterministes derrière les mêmes ports (`IFactExtractor`, `IStoryWriter`, `IStoryVerifier`, `IWikipediaTextClient`), et la voix par `espeak-ng` (`Factory:Tts:Provider=espeak`, ou `auto`). Aucun réseau, aucune clé, aucun coût. Le contenu **livré** vient du snapshot ci-dessous ; les adaptateurs hors ligne servent à faire tourner la chaîne (états, contrôles, audio, publication) et ne produisent pas de texte destiné aux voyageurs.

## Snapshot versionné `data-pipeline/<destination>/`
- `destination.json` (version, mention de relecture) et `pois.json` : pour chaque lieu, coordonnées, vecteur de centres d'intérêt (feuilles de la taxonomie), importance, percentile, « pépite », profil d'affluence, champs éthiques (`fragile`, `accessRegulated`), pages Wikipédia, histoire française (titre, accroche, texte, annonces, note de précaution) et sources.
- **Statut éditorial** : brouillon rédigé par IA à partir de connaissances générales, **sans accès aux sources en ligne** (`review = ai_draft_needs_human_review`). Les affirmations restent générales ; la page Wikipédia est citée comme source de référence, aucune de ses phrases n'est reprise. Les coordonnées sont à quelques dizaines (parfois centaines) de mètres près, les scores sont des estimations. L'application marque ces histoires « générées par IA » (`aiGenerated`) ; une personne doit les relire avant toute mise en avant.
- Écart assumé avec le §8.2 : le snapshot entre dans la machine d'états **directement en `Approved`** (comme si l'éditeur avait approuvé), puis passe par la voix et la publication normales. Ce n'est pas une relecture : c'est un jeu de départ.
- Contrôles automatiques (tests `Factory.UnitTests/SnapshotTests`) : taxonomie, coordonnées dans l'emprise de la destination, phrases ≤ 30 mots, termes interdits, vocabulaire de sécurité, source Wikipédia, notes de précaution pour les lieux fragiles ou à accès réglementé.

## Importeur (`ImportSnapshotCommand`)
- Identifiants **déterministes** (hachage du couple destination/slug, puis du lieu/langue/type/version) : on rejoue le même fichier sur n'importe quelle machine et on retrouve les mêmes lignes, donc les mêmes `poiId` côté Catalog, Discovery et Creators.
- Les lieux sont écrits dans `factory.place` (`source = snapshot`, `osm_type = snapshot`, empreinte SHA-256 du contenu dans le tag `onvoyage:snapshot_sha256`), avec `importance_override` = l'importance du fichier pour qu'un scoring ultérieur ne l'écrase pas. Puis `PublishPlace` : le Catalog, Discovery et Creators reçoivent les mêmes événements que pour un lieu issu d'OSM.
- Histoire : version = empreinte du texte et des sources dans `prompt_version` (`snapshot-1:<hash>`). **Texte inchangé et déjà publié = rien** ; interrompu (Approved / AudioReady) = reprise ; modifié = **nouvelle version** (l'ancienne est archivée par la publication). Un lieu qu'un éditeur a rejeté, dépublié ou fusionné n'est jamais republié.
- **Audio en cache** : un morceau déjà enregistré pour (histoire, voix) n'est pas synthétisé de nouveau. Sans voix disponible (`ITextToSpeechProvider.IsAvailable = false`), l'histoire est publiée **sans audio** (`PublishStoryCommand.AllowTextOnly`) ; dès qu'une voix existe, une nouvelle version porte l'audio. Limite connue : Discovery et l'app ne proposent à l'écoute que les histoires qui ont une partie `main` ; **la lecture vocale sur l'appareil pour une histoire sans audio n'est pas implémentée** (voir « Non fait »).
- Un contexte de messages Wolverine n'envoie qu'une fois : une unité de travail qui publie un lieu **puis** son histoire perdait silencieusement les événements suivants. `PlaceStore.PublishAsync` et `ContentStore.SaveStoryWithEventsAsync` passent leur boîte d'envoi en `MultiFlushMode.AllowMultiples`, et les imports et l'amorçage traitent **chaque lieu dans sa propre portée** (leur unité de travail, leur transaction).

## Amorçage (`BootstrapDestinationCommand`)
- Même code que les étapes manuelles (aucune logique en double) : `ImportPlacesHandler`, `EnrichPlacesHandler`, `ScorePlacesHandler`, `FetchSourcesHandler`, `ExtractFactsHandler`, `WriteStoryHandler`, `GenerateAudioHandler`, `StoryPublicationHandler`.
- **Reprise et idempotence** : chaque étape saute ce qui existe (lieux déjà importés, histoire vivante dans la langue, audio déjà fait, histoire déjà publiée). Relancer la même commande continue où elle s'est arrêtée. Pas de table d'état supplémentaire : l'état est celui des lieux et des histoires.
- **Plafond de coût** : somme de `factory.llm_call.cost_usd` depuis le début de l'exécution comparée à `BudgetUsd` avant chaque lieu et avant chaque appel payant ; arrêt propre (`budget_exhausted`). Refus de démarrer (`prices_missing`) si le fournisseur est payant et qu'aucun tarif n'est configuré, car chaque appel compterait zéro (`AllowUnpriced` pour passer outre).
- **Débit** : lieux traités l'un après l'autre, pause entre deux lieux (`PauseMilliseconds`), reprises en place 10 s / 60 s / 5 min sur panne du fournisseur, arrêt après trois lieux consécutifs en panne (`provider_unavailable`). Les clients Wikimedia gardent leur propre limitation.
- **Publication automatique** (`AutoPublish`, désactivée par défaut) : approuve seulement les histoires `Checked` (toutes vérifications passées), voix, publication. Les `NeedsReview` restent pour une personne.
- Lancement : `dotnet run --project src/Services/Factory/OnVoyage.Factory.Worker -- bootstrap marseille …` (rapport JSON, code de sortie 0 si `completed`) ou `POST /api/factory/v1/admin/bootstrap` (202, exécuté par le worker).

## Fournisseur de voix
`Factory:Tts:Provider` : `openai` (défaut avec le modèle OpenAI), `espeak` (`espeak-ng` en processus séparé, GPL : rien n'est lié ; voix robotique, développement seulement), `none`, `auto`. La normalisation ffmpeg et les balises ID3 (`AI_GENERATED=true`, `TTS_PROVIDER=espeak-ng`) sont les mêmes pour tous.

## Corrections trouvées en faisant tourner tous les services ensemble
- **Catalog** : plusieurs lieux d'une destination nouvelle traités en parallèle insèrent la même ligne `destination` (index unique) ; le message était perdu. L'insertion est maintenant `on conflict do nothing`, et les `DbUpdateException` sont rejouées (200 ms, 1 s, 5 s).
- **Wolverine** : une méthode publique `Validate(...)` d'une classe de gestionnaire est prise pour un middleware de validation (le démarrage échouait sur un type non résolu) : l'outil de contrôle du snapshot s'appelle `FindProblem`.

## Non fait / non vérifié
- Aucun appel réel à OpenAI, Wikipédia, Wikidata, Overpass, Geofabrik ni à la voix OpenAI n'a pu être fait (réseau bloqué) : le chemin en ligne est testé avec de faux clients et n'a jamais tourné pour de vrai. `osm2pgsql` n'est pas installé ici : l'import OSM de l'amorçage n'a pas été exécuté (les tests qui en dépendent sont ignorés).
- Lecture vocale du texte par l'appareil (Web Speech API, `TextToSpeech` MAUI) pour les histoires sans audio : non implémentée. L'app ne joue que les histoires avec audio.
- Tarifs et noms de modèles à renseigner (voir Q-2026-10-01-openai).
- Quand l'amorçage réel importera les lieux OSM de Marseille, les lieux du snapshot seront probablement détectés comme doublons (nom proche, moins de 75 m) et fusionnés par le dédoublonnage du scoring : le lieu conservé est celui qui a un identifiant Wikidata, sinon le plus ancien (`Preference`). Vérifier les fusions proposées dans le back-office après le premier amorçage.
