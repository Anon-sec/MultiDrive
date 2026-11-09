# Composants tiers

MultiDrive Cloud utilise les bibliothèques suivantes, chacune sous sa propre licence. Elles ne sont pas couvertes par
la licence de MultiDrive Cloud (`LICENSE.md`). Vérifiez les versions exactes dans les fichiers `.csproj`.

| Composant | Usage | Licence |
|---|---|---|
| Google.Apis.Drive.v3, Google.Apis, Google.Apis.Auth, Google.Apis.Core | API Google Drive | Apache-2.0 |
| Dapper | Accès SQLite | Apache-2.0 |
| Microsoft.Data.Sqlite, SQLitePCLRaw (dont SQLite) | Base de données locale | MIT / Apache-2.0 ; SQLite est dans le domaine public |
| CommunityToolkit.Mvvm | MVVM (interface) | MIT |
| Microsoft.Extensions.DependencyInjection, Microsoft.Extensions.Logging(.Abstractions) | Injection de dépendances, journalisation | MIT |
| System.Security.Cryptography.ProtectedData | Protection DPAPI des secrets | MIT |
| .NET runtime et WPF (inclus dans l'exécutable autonome) | Exécution et interface | MIT |
| Newtonsoft.Json (dépendance de Google.Apis) | JSON | MIT |
| xUnit, Microsoft.NET.Test.Sdk, coverlet (tests uniquement, non distribués) | Tests | Apache-2.0 / MIT |

Les textes complets des licences sont disponibles dans les paquets NuGet correspondants (https://www.nuget.org) et sur
les dépôts de ces projets. « Google » et « Google Drive » sont des marques de Google LLC ; MultiDrive Cloud n'est ni
affilié à Google ni approuvé par Google.

*Cette liste est fournie à titre informatif ; en cas de doute, la licence publiée avec chaque composant fait foi.*
