# ADR-0008 — Factory : pipeline de contenu (T-301 à T-308)

- Statut : accepté. Couvre les documents sources, l'extraction de faits, la rédaction, les contrôles automatiques, la voix, la publication vers Catalog et les signalements. Complète l'ADR-0007.

## Principe : « faits d'abord, monde fermé » (§8.1)
1. `FetchSources` enregistre les articles Wikipédia du lieu (texte brut, révision, licence CC BY-SA 4.0, date). Un texte inchangé n'est pas ré-enregistré.
2. `ExtractFacts` : une sortie structurée par document. **Un fait n'est gardé que si sa citation figure mot pour mot dans le document** (`QuoteValidator`) ; sinon il est rejeté (`quote_not_found`). Deux sources qui se contredisent mettent le fait en `Conflict` : une personne tranche.
3. `WriteStory` : le rédacteur reçoit **les faits validés et rien d'autre** — `StoryWriteRequest` n'a aucun champ pour un texte source, donc il ne peut pas le paraphraser. Reprises automatiques (couverture des faits, longueur, recouvrement), puis vérificateur indépendant phrase par phrase, contrôle de style/sécurité et recouvrement textuel (plus longue suite < 8 mots **et** Jaccard 5-grammes < 0,03). Tout problème → `NeedsReview` ; sinon `Checked`.
4. Une personne approuve → `GenerateAudio` (jamais avant) → écoute → `PublishStory`. Rien n'est publié ni voisé automatiquement. Machine d'états du §8.2 dans `ContentTransitions`.
5. Voix : un appel de synthèse par partie (texte, intro, 3 annonces), idempotent (une partie déjà stockée n'est pas re-synthétisée). ffmpeg : normalisation loudnorm en deux passes à −16 LUFS, mono 44,1 kHz, MP3 48 kbit/s, ID3 (`AI_GENERATED`, id et version du contenu, fournisseur TTS).
6. Publication : `StoryPublishedV1` / `StoryUnpublishedV1` / `StoryArchivedV1` par l'outbox vers la file `catalog`. Catalog les projette dans `story` (parties audio et sources en jsonb), ré-essaie si le lieu n'est pas encore arrivé (`PoiNotProjectedException`), sert les MP3 sous `/media` et ajoute les sources aux attributions.
7. Signalements (F-20) : `POST /api/factory/v1/stories/{id}/reports` (voyageur, 10/jour) ; 3 voyageurs distincts suspendent l'histoire.

## Choix
- Fournisseur : `OpenAI` (`Microsoft.Extensions.AI`, sortie structurée JSON schema, taxonomie injectée dans le schéma via `__TAXONOMY__`) ou `disabled` (développement). Au démarrage, `openai` exige la clé et les quatre modèles `Factory:Llm:{Extractor,Writer,Verifier,Classifier}Model` : un worker qui ne peut pas appeler le modèle n'accepte pas de travail.
- Prompts versionnés dans `prompts/*.md` (en-tête, sections Système/Utilisateur/Schéma), embarqués dans l'assembly ; la version est écrite sur chaque histoire.
- Chaque appel (modèle, jetons, coût estimé, durée) est une ligne `llm_call` et alimente la métrique `onvoyage.llm.cost_usd`.
- Erreurs fournisseur : `ExternalServiceException` → reprises à 10 s, 60 s, 5 min puis file des échecs ; à la dernière tentative l'histoire passe en `Failed`.

## Écarts avec le cahier des charges
- Les modèles viennent de la configuration (`appsettings`/paramètres Aspire), pas de la configuration distante.
- Les médias sont sur disque local (`Factory:MediaDirectory`, servi par Catalog via `Media:RootPath`) ; un stockage objet/CDN viendra ensuite.
- **Rien n'a été testé contre OpenAI** (réseau bloqué) : les adaptateurs sont testés avec de faux `IChatClient`/ports. Le SDK marque `SpeechGenerationOptions.Instructions` « évaluation » (OPENAI001, supprimé localement). Les noms de modèles et les tarifs sont à renseigner.
- Pas d'interface Blazor d'administration (T-402) : API seulement.

## Lots de génération (T-305)
- `POST /api/factory/v1/admin/batches` choisit au plus 200 lieux (candidats ou publiés, importance minimale facultative, les plus importants d'abord, **sans** histoire vivante dans cette langue et ce type) et dépose un job par lieu dans la file `factory`, dans la même transaction que le lot (outbox). `GET /batches`, `GET /batches/{id}` (progression : terminés, à relire, échecs, en cours), `POST /batches/{id}/retry` (relance seulement les jobs en échec).
- Un job enchaîne sources → faits → rédaction avec les mêmes gestionnaires que les étapes manuelles ; chaque étape est idempotente, donc une reprise continue où elle s'est arrêtée. Une panne de fournisseur est reprise par la file (10 s, 60 s, 5 min, configurable : `Factory:Retry:DelaysSeconds`) ; à la **3ᵉ tentative** le job passe en `Failed` et part en lettre morte (`BatchJobExhaustedException`, jamais reprise). Un refus métier (pas assez de faits) fait échouer le job tout de suite, sans nouvelle tentative.
- Un lot n'écrit que des textes contrôlés : jamais de voix ni de publication (une personne approuve d'abord). Le parallélisme du worker est borné (`Factory:Jobs:MaxParallel`, 4 par défaut) pour limiter les appels payants.
- **Option API Batch d'OpenAI (`use_batch_api`) : non faite.** Elle demande un client dédié (fichier JSONL, suivi d'un lot asynchrone sous 24 h) qui n'a pu être essayé faute d'accès réseau ; les jobs passent par les appels synchrones. La notification à l'administrateur des jobs en lettre morte n'est pas faite non plus : ils sont visibles dans l'écran des lots.
