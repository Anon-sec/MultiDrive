# Tests

```powershell
dotnet test -c Release
```

## Tests automatiques (sans compte Google)

Ils utilisent un **fournisseur de stockage simulé** (`tests/MultiDriveCloud.Tests/Support/FakeStorageProvider.cs`) avec injection
de pannes (capacité, réseau coupé, autorisation révoquée, corruption, session d'envoi reprenable, blocage en cours d'envoi)
et une **vraie base SQLite** + de vrais fichiers dans un dossier temporaire.

| Exigence | Tests (classe › méthode) |
|---|---|
| Ajouter plusieurs comptes, lire les capacités | `AccountTests` |
| Répartition sur plusieurs Drive | `AllocatorTests.Files_are_spread_over_several_accounts_automatically` |
| Refus si espace insuffisant | `AllocatorTests.Allocation_is_refused_when_space_is_insufficient_and_nothing_is_queued` |
| Autre Drive si un compte est plein | `AllocatorTests.Another_drive_is_used_when_one_reports_full_during_upload` |
| Découpage + reconstruction d'un gros fichier | `ChunkTests.Big_file_is_split_over_three_accounts_and_rebuilt_identically` |
| Vérification SHA-256 / corruption | `IntegrityTests` |
| Coupure réseau simulée | `ResilienceTests.Temporary_network_errors…`, `Long_outage…` |
| Reprise après redémarrage | `ResilienceTests.Interrupted_upload_resumes_after_restart…` |
| Compte déconnecté | `ResilienceTests.Disconnected_account…`, `AccountTests.Disconnected_account…` |
| Suppression d'un fichier réparti | `VirtualFileSystemTests.Deleting_a_distributed_file…`, `Failed_remote_deletion…` |
| Préservation des fichiers en cas d'erreur | `ResilienceTests.File_modified_during_upload…`, tests de panne (le fichier source existe toujours) |
| Dossier complet | `VirtualFileSystemTests.Whole_folder_tree_uploads_and_downloads…` |
| Restauration des métadonnées | `IndexBackupTests` |
| Chiffrement AES-256-GCM, clés | `EncryptionTests` |
| Jetons non écrits en clair, journaux masqués | `SecretHandlingTests` |
| OAuth : PKCE (vecteur RFC 7636), `state` falsifié rejeté, refus utilisateur, expiration | `GoogleTests` |

## Test d'intégration réel (comptes Google autorisés)

`GoogleIntegrationTests.Two_real_accounts_distribute_and_restore_a_file` est **ignoré par défaut**. Il :

1. lit dans votre vraie base les comptes Google déjà connectés dans l'application (≥ 2 requis), avec leurs jetons DPAPI
   (même utilisateur Windows) ;
2. utilise une base temporaire (votre index n'est pas touché) ;
3. envoie un fichier aléatoire de 12 Mo en fragments de 5 Mo sur les deux comptes, le télécharge, compare octet par octet,
   puis supprime les fragments de vos Drive.

```powershell
$env:MDC_INTEGRATION = "1"
dotnet test -c Release --filter "FullyQualifiedName~GoogleIntegrationTests"
```

Avant : lancez l'application, configurez OAuth (voir `GOOGLE_OAUTH.md`) et connectez deux comptes.
