# Guide des agents — SlimVector

## Portée

Ce fichier s’applique à tout le dépôt. Un `AGENTS.md` placé plus bas dans
l’arborescence peut préciser ces règles pour son répertoire.

SlimVector est un moteur de données : la justesse, la durabilité, la compatibilité
et les performances priment sur la commodité d’une modification locale.

## Avant toute modification

- Lire le code, les tests et la documentation concernés avant de proposer une
  solution.
- Rechercher avec `rg` ou `rg --files` et réutiliser les abstractions déjà
  présentes.
- Vérifier `git status` et préserver toutes les modifications existantes qui ne
  font pas partie de la tâche.
- Choisir le plus petit changement cohérent qui règle la cause du problème.
- Ne jamais manipuler les données réelles de `src/SlimVector.Studio/data`, les
  sauvegardes ou les modèles locaux pour tester. Utiliser un répertoire temporaire
  unique et le nettoyer.

## Architecture et code

- Respecter les responsabilités des projets `Domain`, `Storage`, `Indexing`,
  `Raft`, `Replication`, `Application`, `Api`, `DocIngestor` et `Studio`.
- Garder les règles métier hors des points d’entrée HTTP et de l’interface.
- Ne pas introduire de dépendance circulaire ni contourner une abstraction de
  stockage, d’indexation ou de consensus.
- Conserver `Nullable`, les analyseurs et les avertissements traités comme des
  erreurs. Ne pas masquer un avertissement sans justification documentée.
- Propager les `CancellationToken`, employer les API asynchrones sur les chemins
  d’E/S et éviter les attentes synchrones sur des tâches.
- Éviter les allocations, copies, LINQ et journalisations inutiles dans les
  chemins chauds. Une optimisation doit rester lisible et mesurée.
- N’ajouter une dépendance externe que si les outils présents ne suffisent pas.

## Configuration obligatoirement typée

- Ne pas lire des clés de configuration brutes dans les services métier.
- Définir une classe d’options dédiée, avec un `SectionName`, des valeurs par
  défaut explicites et des types adaptés (`TimeSpan`, `Uri`, `enum`, etc.).
- Lier la section par le système d’options .NET et injecter `IOptions<T>`,
  `IOptionsSnapshot<T>` ou `IOptionsMonitor<T>` selon le cycle de vie nécessaire.
- Valider les contraintes avec `IValidateOptions<T>` et `ValidateOnStart`.
- Tester au minimum une configuration valide, les bornes invalides et les
  interactions entre propriétés.
- Ne jamais journaliser de secret. Garder les clés existantes compatibles ou
  fournir une migration et une erreur explicite.
- Bannir les nombres magiques : une valeur réglable appartient à des options
  typées ; une invariant métier appartient à une constante nommée.

## Tests

- Toute correction de bogue doit avoir un test de régression qui échoue avant le
  correctif et réussit après.
- Tout nouveau comportement doit avoir des tests unitaires dans le projet de
  tests propriétaire. Réserver les tests d’intégration aux frontières réelles :
  HTTP, disque, sérialisation, réplication, consensus ou processus.
- Couvrir le chemin nominal, les entrées invalides, l’annulation, les erreurs et
  la compatibilité des données persistées quand ils sont pertinents.
- Rendre les tests déterministes : pas de réseau, pas d’horloge réelle si
  `TimeProvider` convient, pas de `Thread.Sleep`, pas d’ordre implicite et pas de
  répertoire partagé.
- Utiliser `TestContext.Current.CancellationToken` pour les tests asynchrones.
- Vérifier les résultats observables plutôt que les détails d’implémentation.
- Ne pas transformer une mesure de temps instable en assertion unitaire.
- Lancer d’abord le projet et le filtre ciblés, puis la suite complète avant
  livraison.

Exemples :

```bash
dotnet test tests/SlimVector.Studio.Tests/SlimVector.Studio.Tests.csproj \
  --no-restore --filter "FullyQualifiedName~StudioIntegrationTests"

dotnet build SlimVector.slnx --no-restore
dotnet test SlimVector.slnx --no-restore --no-build
```

## Benchmarks : toujours mesurer

- Toute modification d’un chemin chaud, du stockage, d’un index, d’une recherche,
  de la sérialisation, de l’ingestion, du planificateur d’écritures, de Raft ou du
  démarrage doit être benchmarkée.
- Établir une référence avant le changement, puis rejouer exactement le même
  scénario en `Release` après le changement, sur la même machine et avec le même
  jeu de données.
- Utiliser BenchmarkDotNet pour les microbenchmarks et le lanceur `--e2e` pour les
  parcours complets. Ajouter un benchmark représentatif si aucun n’exerce le code
  modifié.
- Mesurer selon le cas : débit, p50/p95/p99, allocations, mémoire résidente,
  taille disque, E/S, rappel, temps de chargement à froid et à chaud, nombre de
  chargements d’index et temps de démarrage.
- Pour un changement hors chemin d’exécution, comme du texte ou de la
  documentation, exécuter au minimum le test ou contrôle ciblé et indiquer
  explicitement pourquoi aucun benchmark de calcul n’est pertinent.
- Ne jamais benchmarker sur les données de l’utilisateur. Générer un corpus
  reproductible dans un répertoire temporaire.
- Conserver les commandes, le profil, les résultats avant/après et l’écart dans
  le compte rendu. Ne pas conclure à une amélioration sur une seule mesure.
- Une régression non expliquée n’est pas livrable. Si le bruit masque l’écart,
  augmenter les répétitions.

Exemples :

```bash
dotnet run -c Release --project benchmarks/SlimVector.Benchmarks -- \
  --filter "*FlatIndexBenchmarks*"

dotnet run -c Release --project benchmarks/SlimVector.Benchmarks -- \
  --e2e --profile Smoke --output artifacts/benchmarks
```

## Stockage, index et compatibilité

- Préserver la lecture des manifestes, segments, journaux et sauvegardes déjà
  produits. Toute évolution de format doit être versionnée, migrable et testée.
- Les écritures durables doivent rester atomiques ou récupérables après un arrêt
  brutal. Ne pas remplacer une écriture sûre par une écriture plus rapide sans
  protocole de récupération.
- Une route de métadonnées ou de comptage ne doit pas rejouer les segments ni
  charger un index complet. Les lectures ne doivent ni reconstruire ni persister
  silencieusement un index.
- Documenter l’invalidation des caches et éviter d’exposer une collection mutable
  partagée.
- Tester le chargement à froid, la réouverture, l’annulation et les données
  historiques après toute modification de persistance.

## API et Studio

- Garder les contrats HTTP rétrocompatibles. Préférer des DTO typés et une
  validation à la frontière.
- Retourner des codes d’erreur stables et des messages utiles sans exposer
  d’informations sensibles.
- L’interface visible du Studio est en français. Les identifiants de protocole,
  valeurs d’énumération et noms de champs API restent stables.
- Échapper tout contenu utilisateur injecté dans le DOM et préserver les libellés
  accessibles, le clavier et le contraste.
- Ne pas télécharger de modèle ou appeler un service distant dans un test.

## Définition de terminé

Avant de conclure :

1. Le comportement demandé et les cas d’erreur sont couverts.
2. Les tests ciblés passent.
3. Le benchmark pertinent a été rejoué et ses résultats ont été examinés.
4. `dotnet build SlimVector.slnx --no-restore` passe sans avertissement.
5. `dotnet test SlimVector.slnx --no-restore --no-build` passe.
6. `git diff --check` ne signale aucune erreur.
7. Aucun serveur, processus, fichier temporaire ou artefact de test n’est laissé.
8. Le compte rendu cite les fichiers modifiés, les tests exécutés, les benchmarks
   et toute limite connue.

N’exécuter `dotnet restore` que si les dépendances ont changé ou si les actifs ne
sont pas disponibles localement.
