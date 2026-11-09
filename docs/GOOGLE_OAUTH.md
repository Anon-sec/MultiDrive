# Configurer l'accès Google (une seule intervention de votre part)

MultiDrive Cloud ne demande **jamais** votre mot de passe Google : la connexion se fait dans votre navigateur, sur
`accounts.google.com`, via OAuth 2.0 avec PKCE (S256) et un paramètre `state` anti-falsification.

Pour que Google accepte l'application, il faut lui donner une **identité** (un « client OAuth »). Il n'existe pas
d'identité partagée embarquée dans le logiciel : vous créez la vôtre, gratuitement, en 5 minutes. C'est la seule étape manuelle.

## Permission demandée

| Scope | Pourquoi |
|---|---|
| `https://www.googleapis.com/auth/drive.file` | Accès **uniquement aux fichiers créés par MultiDrive Cloud** (dossier « MultiDrive Cloud » de chaque Drive). L'application ne peut pas lire le reste de votre Drive. |

C'est un scope *non sensible* : aucune validation par Google n'est requise.

> **Raccourci** : dans l'application, la page *Comptes Google* affiche un assistant : 4 boutons ouvrent directement chaque page de la console, puis « Importer automatiquement le JSON téléchargé » retrouve le fichier client_secret_….json dans votre dossier Téléchargements. Le détail ci-dessous explique ce que chaque étape fait.

## Étapes dans Google Cloud Console

1. Ouvrez <https://console.cloud.google.com/> et connectez-vous (n'importe lequel de vos comptes).
2. **Créer un projet** : menu en haut › *Nouveau projet* › nom : `MultiDrive Cloud`.
3. **Activer l'API** : *API et services* › *Bibliothèque* › cherchez **Google Drive API** › *Activer*.
4. **Écran de consentement OAuth** : *API et services* › *Écran de consentement OAuth*
   - Type d'utilisateur : **Externe**.
   - Nom de l'application : `MultiDrive Cloud`, e-mail d'assistance et de contact : le vôtre.
   - Scopes : ajoutez `.../auth/drive.file`.
   - **État de publication : cliquez sur « Publier l'application » (passer en *Production*).**
     > ⚠ En mode **Test**, Google expire les autorisations après **7 jours** : vos comptes passeraient en
     > « Autorisation à renouveler » chaque semaine. Comme `drive.file` n'est pas sensible, la publication est immédiate
     > et ne demande aucune vérification (un avertissement « application non vérifiée » peut apparaître à la connexion :
     > *Paramètres avancés* › *Accéder à MultiDrive Cloud*).
   - Si vous restez en mode Test, ajoutez **chacun de vos comptes** dans *Utilisateurs de test*.
5. **Créer l'identifiant** : *Identifiants* › *Créer des identifiants* › *ID client OAuth* › type **Application de bureau**
   › nom `MultiDrive Cloud Desktop` › *Créer*.
6. Cliquez sur **Télécharger le JSON** (ou notez l'ID client et le code secret).

## Donner l'identité à MultiDrive Cloud

Choisissez **une** de ces méthodes :

- **Dans l'application (recommandé)** : *Paramètres* › *Google OAuth* › **Importer le JSON de Google…**
  (ou collez l'ID client et le secret › *Enregistrer*). Le secret est chiffré par Windows (DPAPI).
- **Fichier** : copiez `google-oauth.example.json` en `google-oauth.json` dans `%LOCALAPPDATA%\MultiDriveCloud\`
  (ou à côté de l'exécutable) et remplacez les valeurs. Le JSON téléchargé depuis Google est aussi accepté tel quel.
- **Variables d'environnement** : `MULTIDRIVE_GOOGLE_CLIENT_ID` et `MULTIDRIVE_GOOGLE_CLIENT_SECRET`.

### À propos du « secret client »

Pour une application *de bureau*, Google précise que ce secret **n'est pas confidentiel** (il ne peut pas l'être dans un
programme distribué) ; la sécurité repose sur PKCE. MultiDrive Cloud ne l'embarque néanmoins jamais dans l'exécutable :
chaque utilisateur fournit le sien et il est stocké protégé.

## Ajouter vos comptes

*Comptes Google* › **Ajouter un compte Google** › le navigateur s'ouvre › choisissez le compte › cochez l'accès à Google Drive ›
retour dans l'application. Répétez avec chaque compte (au moins deux pour profiter de la répartition).

## Quotas et limites à connaître

- Google limite l'envoi à environ **750 Go / jour / compte** ; au-delà, l'application met le compte de côté pour 6 h et
  répartit sur les autres (elle ne cherche jamais à contourner une restriction).
- Le quota de stockage d'un compte est partagé avec Gmail et Google Photos ; l'application lit le quota réel.
- Les sessions d'envoi reprenables expirent après 7 jours ; au-delà, le fragment concerné est renvoyé depuis le début.
- Retirer l'accès depuis <https://myaccount.google.com/permissions> met le compte en « Autorisation à renouveler » ;
  les fichiers restent intacts sur le Drive.
