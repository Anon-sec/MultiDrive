# MultiDrive Cloud

Application Windows (C# / .NET / WPF) qui réunit plusieurs comptes **Google Drive** en un seul espace de stockage virtuel :
vous importez des fichiers ou des dossiers, le logiciel les répartit automatiquement entre vos comptes (en les découpant
en fragments si nécessaire), puis les reconstitue à l'identique au téléchargement, avec vérification SHA-256.

> **État d'avancement honnête** — voir [la section « Ce qui est vérifié »](#ce-qui-est-vérifié-et-ce-qui-ne-lest-pas) :
> le moteur est couvert par 69 tests automatiques réussis (fournisseur simulé) ; la connexion à de vrais comptes Google et
> l'affichage de l'interface n'ont **pas** pu être exécutés dans l'environnement où le code a été écrit.

## Fonctionnalités

- **Comptes** : ajout de N comptes Google (OAuth 2.0 + PKCE + `state`, navigateur officiel, scope minimal `drive.file`),
  quota réel, actualisation manuelle + automatique, expiration/révocation détectée (« Autorisation à renouveler »),
  déconnexion avec révocation du jeton. Aucun mot de passe n'est saisi ni stocké.
- **Cloud unifié** : capacité totale, espace réellement libre (quota réel de chaque compte, pas « 15 Go partout »),
  espace occupé par l'application, marge de sécurité configurable.
- **`StorageAllocator`** : choisit le Drive (stratégies : plus grand espace libre, utilisation équilibrée, meilleur
  ajustement — extensible via `IAllocationStrategy`), **réserve** l'espace pendant le transfert, bascule vers un autre
  compte si un Drive est plein, révoqué ou refuse l'envoi.
- **`ChunkManager`** : découpe en fragments de taille configurable, répartis sur plusieurs comptes quand aucun ne peut
  contenir le fichier. Lecture/écriture **en flux**, jamais de fichier entier en mémoire.
- **`TransferManager`** : file asynchrone, transferts simultanés configurables, pause/reprise/annulation, nouvelles
  tentatives avec attente progressive, **sessions d'envoi reprenables Google** persistées (reprise après redémarrage),
  vitesse, progression globale/par fichier, temps restant, journal des erreurs.
- **Vérifications** : une lecture donne le SHA-256 du fichier et de chaque fragment ; Drive doit confirmer taille **et** MD5
  de chaque fragment avant qu'il soit « stocké » ; au téléchargement, SHA-256 de chaque fragment puis du fichier complet
  avant publication atomique (`.mdcpart` → fichier final). Un fragment manquant/corrompu = erreur explicite, jamais de faux succès.
- **Explorateur** : dossiers virtuels, import fichiers/dossiers (glisser-déposer Windows inclus), téléchargement de
  fichiers/dossiers, renommer, déplacer (glisser-déposer interne), rechercher, trier, sélection multiple, menu contextuel,
  suppression avec confirmation, propriétés. Un fichier réparti n'apparaît que comme **un seul fichier**.
- **Sécurité** : jetons et clés protégés par DPAPI Windows, chiffrement **facultatif AES-256-GCM** par fragment (clé
  aléatoire, exportable avec phrase secrète, jamais codée en dur), validation des chemins, journaux sans secrets,
  jamais de suppression du fichier source.
- **Fiabilité** : SQLite en WAL avec transactions et migrations, sauvegarde/restauration de l'index, export JSON de
  récupération, suppressions reprises si un Drive est injoignable, fichier source modifié pendant l'envoi détecté.

## Prérequis

- Windows 10/11 x64.
- SDK .NET 9 (ou .NET 10 : changer `MdcTfm` dans `Directory.Build.props` en `net10.0`).
- Un identifiant OAuth Google — **seule intervention manuelle**, 5 minutes : [docs/GOOGLE_OAUTH.md](docs/GOOGLE_OAUTH.md).

## Compiler

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet publish src/MultiDriveCloud.App/MultiDriveCloud.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

ou tout d'un coup :

```powershell
.\scripts\build.ps1               # build + tests + publication dans dist\MultiDriveCloud\MultiDriveCloud.exe
.\scripts\build.ps1 -Installer    # + installateur (nécessite Inno Setup 6) : dist\MultiDriveCloud-Setup.exe
```

L'exécutable de développement se trouve dans `src\MultiDriveCloud.App\bin\Debug\net9.0-windows\MultiDriveCloud.exe`.

> **Si Windows refuse de lancer l'exécutable** (« Une stratégie de contrôle d'application a bloqué ce fichier ») :
> c'est Smart App Control / WDAC / AppLocker, qui bloque les programmes non signés. Options : signer l'exécutable avec un
> certificat de signature de code approuvé par votre organisation (`signtool sign /fd SHA256 /a /tr <horodatage> /td SHA256 MultiDriveCloud.exe`),
> faire ajouter une règle d'autorisation par votre administrateur, ou lancer depuis Visual Studio. Le projet ne contourne
> pas cette protection.

## Premier lancement

1. *Comptes Google* : l'assistant en 4 étapes ouvre chaque page de Google Cloud Console, puis importe automatiquement le JSON téléchargé (détails : docs/GOOGLE_OAUTH.md).
2. *Comptes Google › Ajouter un compte Google* (deux fois au minimum).
3. *Fichiers* : glissez-déposez des fichiers ou dossiers. Le suivi est dans *Transferts*.
4. Sélectionnez un fichier › *Télécharger*.

## Données et configuration

| | |
|---|---|
| Dossier de données | `%LOCALAPPDATA%\MultiDriveCloud` (changer avec la variable `MULTIDRIVE_DATA_DIR`) |
| Paramètres | table `Settings` de l'index (Paramètres dans l'interface) |
| Exemple de config OAuth | [config/google-oauth.example.json](config/google-oauth.example.json) |
| Sauvegarde / récupération | [docs/RECOVERY.md](docs/RECOVERY.md) |
| Tests | [docs/TESTING.md](docs/TESTING.md) |

## Architecture

```
src/
├── MultiDriveCloud.Core/            Domaine, sans dépendance à Windows ni à Google
│   ├── Models/  Interfaces/  Settings/  Util/
│   ├── Storage/    StorageAllocator, ChunkManager, IntegrityService, AesGcmStreamCipher,
│   │               AccountService, VirtualFileSystemService, FragmentCleaner
│   └── Transfers/  TransferManager (+ Upload / Download), types de suivi
├── MultiDriveCloud.GoogleDrive/     IStorageProvider pour Google Drive
│   ├── Authentication/  OAuth PKCE, jetons, configuration
│   └── Services/        GoogleDriveStorageProvider (Drive API v3, envois reprenables)
├── MultiDriveCloud.Infrastructure/  SQLite (Dapper), migrations, DPAPI, journaux, sauvegarde d'index
├── MultiDriveCloud.App/             WPF + MVVM (CommunityToolkit.Mvvm), thème sombre/clair
tests/MultiDriveCloud.Tests/         xUnit + fournisseur simulé
scripts/build.ps1   installer/MultiDriveCloud.iss   docs/   config/
```

`IStorageProvider` est le point d'extension pour OneDrive, Dropbox, WebDAV ou S3 : le moteur ne connaît que cette interface.

## Ce qui est vérifié, et ce qui ne l'est pas

**Vérifié (exécuté ici)**
- Compilation Debug/Release et publication `win-x64` autonome en un seul fichier (≈ 68 Mo).
- 69 tests automatiques exécutés et réussis, 1 test réel ignoré (comptes Google requis) : répartition, refus d'espace insuffisant, bascule vers un autre
  compte, découpage/reconstruction octet pour octet, SHA-256, corruption, coupure réseau, reprise après redémarrage
  (reprise au milieu d'un fragment), compte déconnecté, suppression d'un fichier réparti, dossier complet,
  sauvegarde/restauration de l'index, chiffrement, secrets, OAuth (PKCE, `state`, refus, délai), mapping des erreurs Google.

**Non vérifié — à faire de votre côté**
- **Connexion à de vrais comptes Google et envois réels** : je n'avais ni identifiant OAuth ni compte. Le code utilise
  l'API officielle (Google.Apis.Drive.v3), mais n'a pas été exécuté contre Google. Le test d'intégration
  `GoogleIntegrationTests` est prêt (voir docs/TESTING.md).
- **Interface graphique à l'exécution** : le lancement de l'exécutable a été bloqué par une stratégie de contrôle
  d'application de la machine de développement ; l'interface compile mais n'a pas été affichée ni testée visuellement.
- **Installateur** : le script Inno Setup est fourni mais Inno Setup n'est pas installé ici, l'installateur n'a pas été généré.
- Le SDK .NET 10 n'étant pas installé, le projet cible **.NET 9** (changement d'une ligne pour .NET 10).

**Limites connues**
- Aucune redondance : perdre un compte qui héberge un fragment unique rend le fichier irrécupérable (la future
  réplication des fragments est prévue par l'architecture, non implémentée).
- Reconstruction automatique de l'index depuis les propriétés posées sur les fragments Drive : non implémentée
  (utilisez la sauvegarde d'index et l'export JSON).
- Un envoi terminé côté Google mais non enregistré localement (arrêt brutal) est détecté à la reprise grâce à la
  propriété `mdcChunk`, mais d'éventuels objets orphelins d'une session abandonnée ne sont pas balayés automatiquement.
- Pas d'intégration à l'Explorateur Windows (lecteur virtuel) ni de glisser-déposer *vers* l'Explorateur.

## Licence

Logiciel libre sous licence **MIT** : utilisation, modification, redistribution et même vente sont autorisées, à condition de conserver la mention de copyright. Voir [LICENSE.md](LICENSE.md).
Composants tiers : [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Pour contribuer : [CONTRIBUTING.md](CONTRIBUTING.md).
Remplacez [YOUR NAME OR HANDLE] dans LICENSE.md par votre nom avant de publier.

## Contacte 
contacte.anon-sec@proton.me

## Donation
LTC Adresse : Li2vH3URmSW5JXj1gbGDMtDqKSn27xvG42
