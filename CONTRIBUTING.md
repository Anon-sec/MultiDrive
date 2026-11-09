# Contribuer à MultiDrive Cloud

Les contributions sont les bienvenues (corrections, tests, traductions, nouveaux fournisseurs de stockage).

## Mise en route

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release        # 78 tests, sans compte Google (fournisseur simulé)
```

Pour tester avec de vrais comptes, créez votre propre identité OAuth (assistant dans l'application, ou
`docs/GOOGLE_OAUTH.md`) et lancez le test d'intégration décrit dans `docs/TESTING.md`.

## Règles du projet

- **Aucun secret dans le dépôt** : jamais de jeton, de mot de passe, de clé de chiffrement, ni de `google-oauth.json`
  (il est dans `.gitignore`). Les journaux ne doivent contenir aucun secret (voir `Redactor`).
- **Ne jamais déclarer un transfert réussi sans confirmation** : taille et empreinte vérifiées, voir `TransferManager`.
- **Jamais de fichier entier en mémoire** : tout passe par des flux.
- Toute correction de bogue ajoute un test qui échouait avant elle.
- Le moteur (`MultiDriveCloud.Core`) ne dépend ni de Windows ni de Google : ajoutez un fournisseur en implémentant
  `IStorageProvider`.
- Messages utilisateur en français, code et commentaires en anglais.

## Proposer un changement

1. Ouvrez une *issue* pour les changements importants.
2. Créez une branche, faites vos modifications, lancez `dotnet test`.
3. Ouvrez une *pull request* décrivant le problème résolu et comment vous l'avez vérifié.

En contribuant, vous acceptez que votre contribution soit publiée sous la licence MIT du projet.

## Signaler une faille de sécurité

N'ouvrez pas d'issue publique : écrivez directement au mainteneur (contact dans le dépôt) avec les étapes pour reproduire.
