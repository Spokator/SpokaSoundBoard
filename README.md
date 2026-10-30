# SpokaSoundBoard

Soundboard moderne pour Windows : diffusez de la musique et des sons **dans votre micro** (Discord, jeux, TeamSpeak…), avec **ducking automatique** (la musique baisse quand vous parlez), des **effets de voix en temps réel** (radio pilote d'hélico, tour de contrôle, talkie-walkie, robot, voix grave/aiguë, écho, réverbération…), l'**import par lien** (YouTube, SoundCloud, MyInstants…) et des **sorties séparées Jeu / Discord**.

## Fonctionnalités

- **Soundboard** : tuiles colorées, glisser-déposer, volume / couleur / boucle / extrait (début-fin) par son, raccourci global par son (clavier ou boutons souris 4/5/molette).
- **Lecteur de musique** : playlist (fichiers ou dossiers entiers), aléatoire, répétition, barre de progression.
- **Import par lien** : collez (ou glissez depuis le navigateur) un lien YouTube, SoundCloud, MyInstants, TikTok, Twitch, MP3 direct… Pour la musique, une playlist entière peut être importée. L'audio est mis en cache sur le PC (`%AppData%\SpokaSoundBoard\Downloads`) grâce à [yt-dlp](https://github.com/yt-dlp/yt-dlp), téléchargé et mis à jour automatiquement ; ffmpeg n'est récupéré que si un site fournit un format à convertir. Respectez les droits des contenus que vous diffusez.
- **Jeu / Discord séparés** : tableau « Qui entend quoi ? » (voix, effets, musique, sons) pour chaque sortie. Exemple : les passagers de l'hélico entendent la playlist et l'effet pilote, vos amis Discord seulement votre voix naturelle (même en mode radio).
- **Ducking** : votre voix passe par-dessus la musique ; seuil, intensité, vitesses de baisse et de remontée réglables.
- **Effets de voix** : 10 presets intégrés + presets personnels ; radio (filtre, saturation, souffle, squelch « pschht », roger beep), hauteur ±12 demi-tons, robot, écho, réverbération, compresseur, porte de bruit.
- **Mode radio (appuyer pour parler)** : votre voix ne passe que quand vous maintenez la touche, avec le grésillement de radio ; la musique continue.
- **Raccourcis globaux** utilisables en jeu (la touche n'est pas bloquée pour le jeu).
- **Retour casque** : vous entendez la musique, et vous pouvez vous écouter pour tester vos effets.
- Formats : MP3, WAV, OGG, FLAC, M4A/AAC, WMA, AIFF.
- Réglages enregistrés automatiquement dans `%AppData%\SpokaSoundBoard`.

## Comment ça marche

```
Votre micro ──► Voix naturelle / avec effets ──┐                 ┌─► CABLE Input ══► « CABLE Output » = micro du jeu
                                               ├─► Mixage ───────┼─► Line 1      ══► « Line 1 »       = micro Discord (optionnel)
Musique + sons (fichiers ou liens) ────────────┘   + ducking     └─► Votre casque (retour)
```

Windows ne permet pas à une application de créer un micro sans pilote : SpokaSoundBoard utilise **VB-CABLE** (gratuit, [vb-audio.com/Cable](https://vb-audio.com/Cable/)). Pour séparer Discord du jeu, il faut un 2e câble : **Virtual Audio Cable Lite** (gratuit pour un usage personnel, [vac.muzychenko.net](https://vac.muzychenko.net/en/download.htm)). Le logiciel peut télécharger et lancer les deux installations en un clic (page **Configuration**).

## Installation

1. Lancez **`SpokaSoundBoard-Setup-<version>.exe`** et cliquez sur Suivant → Installer. Aucun droit administrateur ni .NET n'est nécessaire. Windows peut afficher « Windows a protégé votre ordinateur » (logiciel non signé) : *Informations complémentaires* → *Exécuter quand même*.
2. L'**assistant de configuration** s'ouvre automatiquement :
   - vérifie votre micro (et l'autorisation micro de Windows) ;
   - installe le micro virtuel **VB-CABLE** et, si vous le souhaitez, le 2e micro pour Discord (**Virtual Audio Cable Lite**) avec leurs installateurs officiels ;
   - redémarre le PC si besoin et **se rouvre tout seul** ensuite ;
   - indique (avec un bouton Copier) quel micro choisir dans Discord et dans vos jeux, et ouvre directement les réglages voix de Discord.
3. C'est tout. L'assistant peut être relancé depuis **Configuration**.

L'installeur crée les raccourcis Bureau / menu Démarrer, propose le lancement au démarrage de Windows, inclut yt-dlp, se met à jour par-dessus une version existante (même ouverte) et se désinstalle proprement depuis *Paramètres Windows → Applications* (vos réglages peuvent être conservés).

> Les micros virtuels ne sont pas intégrés silencieusement à l'installeur : la licence de VB-CABLE l'interdit sans accord de son auteur. Ils sont téléchargés depuis les sites officiels et installés avec leurs propres installateurs, en un clic depuis l'assistant.

### Discord

1. Paramètres → Voix et vidéo → Périphérique d'entrée : **CABLE Output** (ou **Line 1** si vous avez séparé Discord du jeu).
2. Désactivez la suppression de bruit (Krisp), l'annulation d'écho et le contrôle automatique du gain.
3. Sensibilité d'entrée : réglez-la au minimum (ou utilisez le push-to-talk de Discord).

### Jeux

Choisissez **CABLE Output** comme micro dans le jeu, ou définissez-le comme micro par défaut dans Windows (Paramètres → Son). Pour l'ambiance pilote : preset **Pilote d'hélico** et **mode radio** sur la même touche que le push-to-talk du jeu.

## Développement

Prérequis : SDK .NET 9 (Windows).

```powershell
dotnet build                                # compile
dotnet test tests\SpokaSoundBoard.Tests     # tests (DSP, mixage, ducking, réglages)
dotnet run --project src\SpokaSoundBoard    # lance l'application
.\scripts\publish.ps1                       # exe autonome dans .\dist
.\scripts\build-installer.ps1               # installeur complet dans .\dist (Inno Setup 6)
```

| Dossier | Contenu |
| --- | --- |
| `src/SpokaSoundBoard.Core` | Moteur audio (NAudio/WASAPI), DSP (effets, ducking, limiteur), modèles et sauvegarde |
| `src/SpokaSoundBoard` | Application WPF (.NET 9, thème Fluent sombre), MVVM (CommunityToolkit.Mvvm), raccourcis globaux |
| `tests/SpokaSoundBoard.Tests` | Tests xUnit |
| `installer/` | Script Inno Setup et images de l'installeur |
| `tools/make_icon.py`, `tools/make_installer_images.py` | Génèrent l'icône, le logo et les images de l'installeur (Pillow) |

Architecture audio : la sortie vers le câble virtuel sert d'horloge maître et appelle `MixerCore.Render`, qui produit en un seul passage le bus « micro virtuel » et le bus « casque ». Le micro et le casque sont reliés par des tampons anti-gigue à latence bornée. Sans câble virtuel, c'est le casque qui pilote le mixage.
