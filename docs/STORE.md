# Publier SpokaSoundBoard sur le Microsoft Store

SpokaSoundBoard part sur le Store en **MSIX**, comme SpokaStream : c'est le Store qui signe le paquet, gratuitement. La voie `.exe` du Store exigerait un certificat de signature payant (règle 10.2.9). La version GitHub (installeur Inno Setup) reste telle quelle : les deux éditions peuvent exister côte à côte.

Pour une version suivante, va directement à [Pour une nouvelle version](#pour-une-nouvelle-version).

**État** : nom réservé le 11/11/2026, produit **9NZH7V1ZS1T1** (https://apps.microsoft.com/detail/9NZH7V1ZS1T1), identité `Spokator.SpokaSoundBoard` / `CN=1FBF4454-E4DA-41D2-BBA3-8C9433B84CE4` (identique au manifeste). Première soumission préparée avec la 2.2.0 : paquet validé, fiche française (5 captures 1920 × 1032 prises dans la VM de test avec l'édition Store), classification IARC 3+. Partner Center limite la justification de `runFullTrust` à 500 caractères : la version envoyée est plus courte que celle ci-dessous.

## Ce qui change dans l'édition Store

L'application détecte qu'elle tourne depuis un paquet (`AppEnvironment.IsPackaged`) et se comporte alors ainsi :

| Sujet | Installeur (GitHub) | Microsoft Store (MSIX) |
| --- | --- | --- |
| Mises à jour | Vérification GitHub, bannière, installeur silencieux | Aucune : « Mises à jour automatiques par le Microsoft Store » |
| Lancement avec Windows | Clé `HKCU\…\Run` | `StartupTask` du manifeste (`SpokaSoundBoardStartup`), désactivée par défaut. Si l'utilisateur l'a coupée dans Paramètres → Applications → Démarrage, un message l'explique et ouvre la page. Lancée par Windows, l'application démarre réduite dans la zone de notification |
| Micro virtuel | Télécharge et lance l'installeur officiel de VB-CABLE / VAC Lite | N'exécute rien : ouvre la page officielle et l'assistant explique les étapes |
| Reprise après redémarrage | Clé `RunOnce` qui rouvre l'assistant | Pas de `RunOnce` : l'étape est enregistrée (`SetupResumeStep`), l'assistant reprend au prochain lancement. Le bouton « Redémarrer maintenant » lance toujours `shutdown.exe`, seulement si l'utilisateur clique |
| Autorisation micro | Entrée `NonPackaged` | Entrée du paquet (`AppCapability`), avec demande d'autorisation Windows |
| yt-dlp, ffmpeg | yt-dlp livré, mis à jour par `yt-dlp -U`, ffmpeg téléchargé au besoin | Les deux sont livrés dans `tools\` du paquet et utilisés en lecture seule (`AppEnvironment.BundledToolsDirectory`) : jamais de `-U` ni de téléchargement d'exécutable (règle 10.2.2) |
| Double-clic sur un `.spokapack` | — | Association de fichier du manifeste : l'application importe le pack |
| Données | `%AppData%\SpokaSoundBoard` | Même chemin vu de l'application, mais Windows redirige les écritures vers le dossier privé du paquet, effacé à la désinstallation |
| À propos | « édition Installeur » | « édition Microsoft Store » |

Pour essayer ces écrans sans paquet : lancer l'application avec la variable d'environnement `SPOKA_FORCE_PACKAGED=1`. Les appels qui demandent une vraie identité de paquet (`StartupTask`, `AppCapability`) sont alors simplement ignorés.

## Ce que tu dois faire

### 1. Le compte

Le compte développeur **Spokator** existe déjà (celui de SpokaStream). Rien à refaire.

### 2. Réserver le nom

Dans [Partner Center](https://partner.microsoft.com/dashboard) : **Applications et jeux**, **Nouveau produit**, **Application MSIX ou PWA**, puis réserve **SpokaSoundBoard**.

### 3. Vérifier l'identité du produit

Dans **Gestion des produits**, **Identité du produit**, compare ces trois valeurs avec `packaging/AppxManifest.xml` :

| Partner Center | Manifeste aujourd'hui |
| --- | --- |
| `Package/Identity/Name` | `Spokator.SpokaSoundBoard` |
| `Package/Identity/Publisher` | `CN=1FBF4454-E4DA-41D2-BBA3-8C9433B84CE4` |
| `Package/Properties/PublisherDisplayName` | `Spokator` |

Le `Publisher` et le nom d'éditeur sont ceux du compte, donc ceux de SpokaStream. **Le `Name` est attribué par Microsoft à la réservation** : il ressemble souvent à `12345Spokator.SpokaSoundBoard`. S'il diffère, remplace-le dans `packaging/AppxManifest.xml` (attribut `Name` de `<Identity>`) avant de construire. Sinon, Partner Center refuse le paquet.

### 4. Construire le paquet

Dans PowerShell, à la racine du dépôt :

```powershell
.\scripts\build-store.ps1
```

Le script n'a besoin ni du kit Windows ni de droits administrateur :

1. lit la version dans `src/SpokaSoundBoard/SpokaSoundBoard.csproj` (`2.0.0` devient `2.0.0.0`, le Store exige un 4e chiffre à 0) ;
2. télécharge une fois `makeappx`, `makepri` et `signtool` (paquet NuGet `Microsoft.Windows.SDK.BuildTools`), `yt-dlp.exe` (renouvelé après 7 jours) et `ffmpeg.exe` (build `win64-gpl` de yt-dlp, renouvelé après 30 jours), le tout dans `packaging/deps` (ignoré par git) ;
3. lance les tests (`-SkipTests` pour les sauter), puis `dotnet publish` en autonome win-x64, **sans fichier unique ni trimming** ;
4. ajoute `tools\yt-dlp.exe`, `tools\ffmpeg.exe` et la licence de ffmpeg, le manifeste versionné, les images de `packaging/Assets` et un `resources.pri` ;
5. produit **`dist-store/SpokaSoundBoard-<version>.msix`**, non signé : c'est le Store qui le signe.

Les images du paquet se régénèrent avec `python tools/make_store_assets.py` si le logo change.

### 5. Remplir la soumission

| Section | Quoi mettre |
| --- | --- |
| Tarification et disponibilité | **Gratuit**, tous les marchés |
| Propriétés | Catégorie **Musique** (sous-catégorie libre), ou **Divertissement**. Politique de confidentialité : `https://github.com/Spokator/SpokaSoundBoard/blob/main/docs/PRIVACY.md`. Site web : `https://github.com/Spokator/SpokaSoundBoard`. Contact d'assistance : `https://github.com/Spokator/SpokaSoundBoard/issues` |
| Propriétés, configuration requise | Cocher **Microphone** (requis) et **Clavier**, **Souris** (recommandés) |
| Classifications par âge | Questionnaire IARC : pas de violence, de sexe ni de jeu d'argent ; **pas** de chat ni d'échange entre utilisateurs dans l'application (Discord est à part) ; **oui** à « accès à Internet sans restriction / contenu en ligne non filtré », puisque la recherche affiche des résultats YouTube et MyInstants ; pas d'achat. Ce contenu non filtré peut relever la classification (12+ par exemple) : c'est normal |
| Packages | Dépose le fichier `.msix` |
| Listes du Store (français) | Copie la fiche ci-dessous et ajoute les captures |
| Options de soumission | Copie les notes de certification ci-dessous |

**Captures** : le Store veut au moins **1366 × 768** pixels. Celles de `docs/screenshots` (`soundboard.png`, `music.png`, `voice.png`, `mix.png`, `search.png`) font 1280 × 820 : refais-les fenêtre agrandie (1920 × 1080 idéalement), dans cet ordre, sans pseudo Discord ni contenu protégé visible. Légendes proposées :

1. `soundboard.png` : « Vos sons en tuiles, avec un raccourci clavier ou souris utilisable en pleine partie. »
2. `music.png` : « Votre musique dans le micro, avec fondu enchaîné, paroles et volume égalisé. »
3. `voice.png` : « 23 effets de voix en temps réel : pilote d'hélico, radio, robot, démon… »
4. `mix.png` : « Qui entend quoi ? Le jeu et Discord peuvent recevoir des choses différentes. »
5. `search.png` : « Cherchez un son ou une musique et ajoutez-le en un clic. »

### 6. Envoyer

Microsoft relit la soumission, en général en quelques jours. S'il y a un refus, envoie-moi le message tel quel.

## Fiche du Store

**Nom** : SpokaSoundBoard

**Description courte**

> Soundboard, musique et effets de voix en temps réel, directement dans votre micro pour Discord et vos jeux.

**Description**

> SpokaSoundBoard envoie vos sons, votre musique et votre voix transformée dans votre micro, pour Discord comme pour vos jeux.
>
> Lancez un son d'une touche, même en pleine partie : la touche n'est pas bloquée, le jeu la reçoit aussi. Mettez votre playlist, parlez par-dessus : la musique baisse toute seule quand vous parlez, puis remonte doucement.
>
> Transformez votre voix avec 22 effets en temps réel, de la radio de pilote d'hélico au robot, en passant par la tour de contrôle et le démon. Le mode radio ne transmet votre voix que quand vous maintenez la touche, avec le grésillement et le bip de fin.
>
> Décidez qui entend quoi : la musique et l'effet pilote pour les joueurs en jeu, votre voix naturelle sur Discord.
>
> Votre téléphone devient un Stream Deck : scannez le QR code, et lancez vos sons, changez d'effet, pilotez la musique ou animez le blind test depuis le canapé, sur votre réseau Wi-Fi, sans rien installer. Les boutons marchent aussi avec un Stream Deck, Touch Portal ou OBS.
>
> SpokaSoundBoard utilise le micro virtuel gratuit VB-CABLE, de VB-Audio, à installer une fois depuis son site officiel. L'assistant de configuration vous guide pas à pas.

**Fonctionnalités** (une par ligne dans Partner Center)

- Soundboard en onglets, avec raccourcis clavier et souris utilisables en jeu
- Sons « moi seulement » joués dans votre casque, écoute avant diffusion
- Enregistrement d'un son avec votre micro
- Recherche intégrée : sons MyInstants, musiques YouTube et SoundCloud
- Lecteur de musique avec file d'attente, fondu enchaîné, paroles et volume égalisé
- Import de playlists Spotify, Deezer, Apple Music, YouTube et SoundCloud
- Blind test à animer dans Discord, avec scores et podium
- Synthèse vocale dans le micro, avec des voix naturelles hors ligne
- La musique baisse automatiquement quand vous parlez
- 22 effets de voix en temps réel, et vos propres presets
- Mode radio : parler en maintenant une touche, avec bip de fin
- Routage séparé pour le jeu et pour Discord
- Télécommande depuis le téléphone, façon Stream Deck, avec trois thèmes
- Compatible Stream Deck, Touch Portal et OBS (adresses HTTP locales)
- Packs de soundboard à partager avec vos amis (.spokapack)
- Thèmes sombre, noir OLED et clair
- Assistant de configuration, zone de notification, lancement avec Windows

**Mots-clés** (7 au plus) : soundboard, effets de voix, modificateur de voix, discord, micro, musique, radio

**Copyright** : © 2026 Spokator

## Si Partner Center demande pourquoi `runFullTrust`

> SpokaSoundBoard is a WPF desktop app (.NET 9) that needs full trust for: (1) real-time WASAPI audio on several devices at once: it captures the microphone, mixes voice effects, music and sounds, and renders the result to up to three outputs (a virtual audio cable for the game, a second one for Discord, and the user's headphones); (2) global hotkeys that work while a game has focus: low-level keyboard and mouse hooks (WH_KEYBOARD_LL / WH_MOUSE_LL) that only compare the key to the user's shortcuts and never swallow or log it; (3) launching the bundled command-line tools yt-dlp.exe and ffmpeg.exe, shipped inside the package, to import audio from links the user pastes or searches. None of this is available to a sandboxed app.

## Notes de certification

> SpokaSoundBoard is a soundboard, music player and real-time voice changer for Discord and games. No account and no test credentials are needed.
>
> To test without any driver: on first launch the setup assistant opens. Pick a microphone, then press "Suivant" until the end (the virtual microphone step can be skipped). In "Soundboard", drag any MP3/WAV file onto the window and click its tile: it plays in the selected output. In "Voix & effets", choose a preset (e.g. "Pilote d'hélico") and speak into the microphone with the headphone monitor enabled. In "Musique", search a song or paste a YouTube link to import it.
>
> Virtual audio cable: to send the mix into Discord or a game, the app relies on VB-CABLE, a free third-party audio driver by VB-Audio (https://vb-audio.com/Cable/). The Store edition never downloads or runs any installer: the assistant only opens the official VB-Audio web page and explains how the user installs the driver himself. The app works without it (local playback and monitoring).
>
> Bundled tools: tools\yt-dlp.exe (Unlicense) and tools\ffmpeg.exe (GPL, license in tools\ffmpeg-LICENSE.txt) are part of the package and are only run when the user searches or imports a link. The Store edition never updates or downloads executables; new versions ship as Store updates. There is no self-update, no telemetry and no account.
>
> Natural text-to-speech voices: the sherpa-onnx / ONNX Runtime libraries (sherpa-onnx-c-api.dll, onnxruntime.dll) are part of the package. When the user clicks "Télécharger" in the text-to-speech options, the app downloads a voice model (data files only: .onnx, .json, .bin, about 139 MB, SHA-256 checked) from Hugging Face into its data folder; no executable code is ever downloaded.
>
> Remote control (off by default): in "Configuration" > "Télécommande", the user can turn on a small local HTTP server (port 8737) so a phone browser or a Stream Deck can trigger sounds and actions. It listens on localhost only, unless the user also ticks phone access, which then listens on the local network (Windows Firewall asks first). Every request needs a random 10-character access code shown in the app (with a QR code); an address that sends 10 wrong codes is temporarily locked out. No cloud service, nothing leaves the local network.
>
> Global hotkeys use low-level keyboard/mouse hooks so they work in full-screen games; keys are passed through to the game unchanged and nothing is recorded.
>
> The source code is public: https://github.com/Spokator/SpokaSoundBoard

## Essai local du paquet (sideloading)

Un paquet à l'identité Store ne s'installe que depuis le Store. Pour l'essayer avant, on construit une variante signée avec un éditeur fictif :

```powershell
.\scripts\build-store.ps1 -TestSign
```

Le script remplace l'éditeur par `CN=SpokaSoundBoard-Dev`, crée (ou réutilise) un certificat autosigné dans le magasin **de l'utilisateur** (`Cert:\CurrentUser\My`, sans droits administrateur), l'exporte en `dist-store/SpokaSoundBoard-Dev.cer` et signe `dist-store/SpokaSoundBoard-<version>-test.msix` avec `signtool`. Ensuite :

1. **Une seule fois, dans un PowerShell lancé en administrateur** (le script ne le fait jamais lui-même) :
   ```powershell
   Import-Certificate -FilePath .\dist-store\SpokaSoundBoard-Dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
   ```
2. Dans un PowerShell normal :
   ```powershell
   Add-AppxPackage .\dist-store\SpokaSoundBoard-2.0.0-test.msix
   ```
3. Lancer **SpokaSoundBoard** depuis le menu Démarrer. Vérifier : À propos affiche « édition Microsoft Store », pas de bannière de mise à jour, l'interrupteur « Lancer au démarrage de Windows » apparaît dans Paramètres → Applications → Démarrage, double-clic sur un `.spokapack`, recherche YouTube (yt-dlp livré).
4. Pour désinstaller : `Get-AppxPackage Spokator.SpokaSoundBoard | Remove-AppxPackage`. Retirer le certificat de test de `TrustedPeople` quand on n'en a plus besoin.

Comme pour SpokaStream, **essaie avec un profil Windows sans données SpokaSoundBoard** si tu veux voir le premier lancement : une application empaquetée lit le vrai `%AppData%\SpokaSoundBoard` de l'édition installeur (elle ne fait qu'écrire ailleurs). Sur un PC où l'édition installeur est déjà configurée, l'édition Store démarre donc avec ses réglages.

## Risques et plan B

- **Fraîcheur de yt-dlp.** Les sites changent souvent et une version de yt-dlp vieille de quelques semaines finit par échouer. L'édition Store n'a pas le droit de se mettre à jour toute seule : **il faut publier une mise à jour Store régulièrement** (toutes les 2 à 4 semaines, ou dès que la recherche YouTube casse), même sans changement de l'application. Le script reprend automatiquement le dernier yt-dlp. Il suffit d'augmenter la version du csproj, de reconstruire et de soumettre. Plan B si c'est trop contraignant : renvoyer vers l'édition GitHub pour l'import de liens.
- **VB-CABLE n'est pas sur le Store.** Le Store ne permet pas d'installer un pilote. Si un testeur estime l'application inutilisable sans lui, répondre que l'écoute locale, la soundboard et les effets marchent sans pilote, et que VB-CABLE est un pilote gratuit qu'on installe depuis le site de son éditeur, comme le font d'autres soundboards du Store. Plan B : mettre en avant ce point dans la description et les notes.
- **`runFullTrust` et hooks bas niveau.** C'est courant (Discord, OBS, autres soundboards), mais Microsoft reste juge. Plan B : si les hooks posent problème, ne garder en édition Store que les raccourcis globaux classiques (`RegisterHotKey`), qui ne nécessitent aucun hook, en perdant les boutons de souris.
- **ffmpeg en GPL.** Le build `win64-gpl` est redistribué dans le paquet : la licence est livrée dans `tools\`, et le code source de ffmpeg est public (lien dans la licence et sur https://github.com/yt-dlp/FFmpeg-Builds). Si on préfère éviter la GPL, la variante `win64-lgpl` du même dépôt suffit pour l'audio : changer l'URL dans `scripts/build-store.ps1`.
- **Taille.** Le paquet pèse environ 175 Mo (runtime .NET autonome, yt-dlp, ffmpeg, moteur des voix naturelles : +21 Mo de DLL, environ +8 Mo compressés ; le modèle de voix de 139 Mo n'est pas dans le paquet, il est téléchargé à la demande) ; le Store le compresse et ne télécharge que les fichiers modifiés lors des mises à jour.
- **Données séparées.** Les deux éditions ne partagent pas leurs écritures : passer de l'une à l'autre conserve les réglages existants au premier lancement (lecture), mais les modifications faites ensuite restent dans l'édition qui les a faites. On peut exporter sa soundboard en `.spokapack` pour la transférer.

## Pour une nouvelle version

1. Augmenter `<Version>` dans `src/SpokaSoundBoard/SpokaSoundBoard.csproj` et publier la version GitHub si besoin.
2. `.\scripts\build-store.ps1` : le fichier sort dans `dist-store/`.
3. Dans Partner Center : **Mettre à jour** la soumission, remplacer le paquet, envoyer.
