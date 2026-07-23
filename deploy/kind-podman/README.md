# SlimVector géorépliqué avec Kind et Podman

Cette configuration lance un environnement local de reprise après sinistre :

| Namespace | Rôle | Instances SlimVector | API locale |
| --- | --- | ---: | --- |
| `slimvector-eu-west` | région primaire accessible en écriture | 3 | `http://127.0.0.1:8090` |
| `slimvector-eu-central` | région secondaire en lecture seule | 3 | `http://127.0.0.1:8091` |

Chaque région forme son propre cluster Raft à trois votants et utilise un facteur
de réplication des données de 3. La géoréplication asynchrone est indépendante de
Raft et envoie les écritures de `eu-west` vers `eu-central`.

Il s'agit d'une simulation locale : les deux régions partagent la même machine
Podman. Elle vérifie la configuration et les parcours de réplication, mais ne
simule pas une panne physique de région.

## Prérequis

- Podman 5 ou plus récent avec une machine démarrée ;
- Kind ;
- `kubectl`, `curl` et OpenSSL.

Sur macOS :

```bash
podman machine start
```

Six processus SlimVector et quatre nœuds Kind sont lancés. Une machine Podman
avec environ 6 processeurs et 8 Gio de mémoire est recommandée. Sa capacité peut
être ajustée lorsqu'elle est arrêtée :

```bash
podman machine stop
podman machine set --cpus 6 --memory 8192
podman machine start
```

Kind sélectionne explicitement Podman avec
`KIND_EXPERIMENTAL_PROVIDER=podman`. Les redirections de ports suivent le modèle
`extraPortMappings` documenté par Kind :

- [fournisseur Podman et démarrage rootless](https://kind.sigs.k8s.io/docs/user/rootless/) ;
- [configuration et redirections de ports](https://kind.sigs.k8s.io/docs/user/configuration/) ;
- [chargement d'une image locale](https://kind.sigs.k8s.io/docs/user/quick-start/#loading-an-image-into-your-cluster).

## Lancement

Depuis la racine du dépôt :

```bash
./deploy/kind-podman/up.sh
```

Le script :

1. crée ou réutilise le cluster `slimvector-geo` ;
2. construit `Dockerfile` avec Podman ;
3. charge l'image dans les quatre nœuds Kind ;
4. détecte et injecte les IP des trois workers dans les contrats Raft ;
5. génère deux secrets locaux distincts pour l'administration et la
   géoréplication ;
6. attend que les six pods soient prêts.

Pour utiliser une image existante sans la reconstruire :

```bash
SLIMVECTOR_IMAGE=ghcr.io/slimplanet/slimvector:latest \
SLIMVECTOR_BUILD_IMAGE=false \
./deploy/kind-podman/up.sh
```

Les secrets sont conservés lors d'un second lancement et ne sont jamais écrits
dans le dépôt.

## Vérification

Afficher les six pods, leur worker et la santé des deux régions :

```bash
./deploy/kind-podman/status.sh
```

Créer une collection et un document dans la région primaire, puis attendre leur
arrivée sur le secondaire :

```bash
./deploy/kind-podman/smoke-test.sh
```

Commandes d'inspection utiles :

```bash
kubectl --context kind-slimvector-geo \
  -n slimvector-eu-west logs daemonset/slimvector --tail=100

kubectl --context kind-slimvector-geo \
  -n slimvector-eu-central logs daemonset/slimvector --tail=100

curl http://127.0.0.1:8090/metrics
curl http://127.0.0.1:8091/metrics
```

Les trois points d'accès de chaque cluster sont exposés pour que les
redirections vers le leader Raft restent accessibles depuis la machine hôte :

| Membre | `eu-west` | `eu-central` |
| ---: | --- | --- |
| 0 | `http://127.0.0.1:8090` | `http://127.0.0.1:8091` |
| 1 | `http://127.0.0.1:8092` | `http://127.0.0.1:8192` |
| 2 | `http://127.0.0.1:8093` | `http://127.0.0.1:8193` |

## Choix d'implémentation

SlimVector exige actuellement des adresses IP pour les transports Raft. Les pods
utilisent donc `hostNetwork` et un processus de chaque région est placé sur
chacun des trois workers. Les ports de `eu-central` sont décalés pour permettre
aux deux régions de partager les mêmes workers :

| Usage | `eu-west` | `eu-central` |
| --- | ---: | ---: |
| HTTP | 8080 | 8180 |
| catalogue Raft | 3262 | 3362 |
| groupes de données initiaux | 3263–3264 | 3363–3364 |

Les données sont placées dans `/var/lib/slimvector/<region>` à l'intérieur de
chaque conteneur worker Kind. Elles survivent au redémarrage d'un pod, mais sont
supprimées avec le cluster Kind. Cette stratégie est volontairement locale et
ne doit pas être copiée telle quelle en production.

La réception géographique est épinglée sur le premier worker secondaire afin
que son checkpoint soit stable. Les trois membres secondaires participent
néanmoins au consensus Raft et conservent chacun une réplique des données.

## Arrêt et suppression

La commande suivante supprime le cluster et toutes les données qu'il contient :

```bash
./deploy/kind-podman/down.sh
```

Pour arrêter temporairement l'environnement sans supprimer le cluster, arrêtez
la machine Podman. Si les adresses internes des workers changent après une
recréation de la machine, recréez également le cluster Kind : les adresses Raft
font partie de son état persistant.
