# Sauvegarde, restauration et récupération

## Ce qui est stocké où

| Donnée | Emplacement | Contenu sensible ? |
|---|---|---|
| **Index** (fichiers, dossiers, fragments, transferts) | `%LOCALAPPDATA%\MultiDriveCloud\index.db` (SQLite) | Noms de fichiers, empreintes. Pas de jeton. |
| Jetons Google | `%LOCALAPPDATA%\MultiDriveCloud\secrets\*.bin` | Chiffrés par Windows (DPAPI, utilisateur courant) |
| Clé de chiffrement (si activé) | même dossier, même protection | Oui |
| Fragments | Dossier « MultiDrive Cloud » de chaque Drive | Contenu des fichiers (chiffré si l'option est active) |
| Journaux | `%LOCALAPPDATA%\MultiDriveCloud\logs\` | Aucun jeton (masqués avant écriture) |

Les fragments sont nommés `<id-fichier>.<index>.mdc` : **le nom d'origine n'apparaît jamais sur Google Drive.**

## 1. Sauvegarder l'index (à faire régulièrement)

*Paramètres › Sauvegarde et récupération › Sauvegarder l'index…* crée une copie cohérente (`VACUUM INTO`, sans arrêter
les transferts). Rangez-la **hors** de ce PC (autre disque, clé USB, coffre).

## 2. Restaurer l'index

*Paramètres › Restaurer l'index…* : le fichier est d'abord vérifié (`integrity_check`, tables attendues, version),
l'index actuel est copié en `index-before-restore-AAAAMMJJ-HHMMSS.db`, puis remplacé. L'application redémarre.

Après restauration sur un **autre ordinateur / profil Windows**, les jetons ne sont pas transférables (DPAPI) :
reconnectez chaque compte avec **le même compte Google** (*Renouveler l'autorisation*). Les fichiers redeviennent
téléchargeables.

## 3. Export de récupération (JSON)

*Export de récupération* produit un fichier lisible : chemin virtuel, taille, SHA-256, et pour chaque fragment : compte,
identifiant Drive, taille, empreinte, nom distant. **Il ne contient aucun jeton ni clé.** Il sert à reconstituer
manuellement un fichier si l'application n'est plus disponible : télécharger les fragments depuis les Drive indiqués,
les concaténer dans l'ordre `index` croissant, vérifier le SHA-256 (si le fichier est chiffré, voir ci-dessous).

## Que se passe-t-il si… ?

| Perte | Conséquence | Récupération |
|---|---|---|
| **Index perdu**, sauvegarde existante | Aucune perte | Restaurer la sauvegarde. Les fichiers ajoutés après elle restent sur les Drive mais ne sont plus référencés. |
| **Index perdu, aucune sauvegarde** | Les fragments sont toujours sur vos Drive, mais on ne sait plus quel fragment va avec quel fichier ni dans quel ordre : **fichiers irrécupérables en pratique** (si un export JSON existe, il suffit). Les propriétés `mdcFile` / `mdcIndex` / `mdcCount` posées sur chaque fragment permettraient de le reconstituer, mais l'application n'implémente pas encore cet outil de reconstruction. | Sauvegardez ! |
| **Compte Google perdu / suspendu / supprimé** | Les fragments hébergés par ce compte sont perdus ; **tout fichier dont au moins un fragment s'y trouve est irrécupérable** (pas de redondance dans cette version). Les fichiers dont tous les fragments sont ailleurs ne sont pas touchés. | Aucune. Ne retirez pas un compte avant d'avoir re-téléchargé ses fichiers. |
| **Autorisation expirée / révoquée** | Aucune perte | *Comptes Google › Renouveler l'autorisation*. |
| **Clé de chiffrement perdue** (profil Windows détruit, réinstallation…) | Fichiers chiffrés **définitivement illisibles** (AES-256-GCM, aucune porte dérobée) | Importer le fichier de clé exporté (*Paramètres › Chiffrement › Importer une clé*) avec sa phrase secrète. Exportez-la dès l'activation ! |
| Fragment supprimé à la main sur un Drive | Le fichier concerné n'est plus téléchargeable (erreur explicite « fragment introuvable ») | Restaurer le fragment depuis la corbeille Google Drive s'il y est encore. |

## Format de la clé exportée

JSON : `kdf = PBKDF2-SHA256` (600 000 itérations), sel et nonce aléatoires, clé enveloppée en AES-256-GCM. Sans la phrase
secrète (≥ 10 caractères) le fichier est inutilisable ; ne les rangez pas au même endroit.

## Format d'un fragment chiffré (`MDC1`)

`"MDC1" | sel(16) | taille_segment(4) | [ chiffré | tag(16) ]…` — clé de fragment = HKDF-SHA256(clé maîtresse, sel),
nonce = compteur de segment, le dernier segment est authentifié « final » (détecte les troncatures).
