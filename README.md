# SpokaSoundBoard

Soundboard moderne pour Windows : diffusez de la musique et des sons **dans votre micro** (Discord, jeux, TeamSpeak…), avec **ducking automatique** (la musique baisse quand vous parlez) et des **effets de voix en temps réel** (radio pilote d'hélico, tour de contrôle, talkie-walkie, robot, voix grave/aiguë, écho, réverbération…).

## Fonctionnalités

- **Soundboard** : tuiles colorées, glisser-déposer, volume / couleur / boucle par son, raccourci global par son (clavier ou boutons souris 4/5/molette).
- **Lecteur de musique** : playlist (fichiers ou dossiers entiers), aléatoire, répétition, barre de progression.
- **Ducking** : votre voix passe par-dessus la musique ; seuil, intensité, vitesses de baisse et de remontée réglables.
- **Effets de voix** : 10 presets intégrés + presets personnels ; radio (filtre, saturation, souffle, squelch « pschht », roger beep), hauteur ±12 demi-tons, robot, écho, réverbération, compresseur, porte de bruit.
- **Mode radio (appuyer pour parler)** : votre voix ne passe que quand vous maintenez la touche, avec le grésillement de radio ; la musique continue.
- **Raccourcis globaux** utilisables en jeu (la touche n'est pas bloquée pour le jeu).
- **Retour casque** : vous entendez la musique, et vous pouvez vous écouter pour tester vos effets.
- Formats : MP3, WAV, OGG, FLAC, M4A/AAC, WMA, AIFF.
- Réglages enregistrés automatiquement dans `%AppData%\SpokaSoundBoard`.

## Comment ça marche

```
Votre micro ──► Effets de voix ──┐
                                 ├─► Mixage + ducking ──► CABLE Input ══► « CABLE Output » = micro pour Discord / jeux
Musique + sons ──────────────────┘                    └─► Votre casque (retour)
```

Windows ne permet pas à une application de créer un micro sans pilote : SpokaSoundBoard utilise **VB-CABLE** (gratuit, [vb-audio.com/Cable](https://vb-audio.com/Cable/)). Le logiciel peut le télécharger et lancer son installation en un clic (page **Configuration**).

## Installation

1. Lancez `dist\SpokaSoundBoard.exe` (aucune installation de .NET nécessaire).
2. Page **Configuration** → **Installer VB-CABLE automatiquement** → cliquez sur « Install Driver » → redémarrez le PC.
3. Relancez SpokaSoundBoard : le micro virtuel est détecté automatiquement.

### Discord

1. Paramètres → Voix et vidéo → Périphérique d'entrée : **CABLE Output**.
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
```

| Dossier | Contenu |
| --- | --- |
| `src/SpokaSoundBoard.Core` | Moteur audio (NAudio/WASAPI), DSP (effets, ducking, limiteur), modèles et sauvegarde |
| `src/SpokaSoundBoard` | Application WPF (.NET 9, thème Fluent sombre), MVVM (CommunityToolkit.Mvvm), raccourcis globaux |
| `tests/SpokaSoundBoard.Tests` | Tests xUnit |
| `tools/make_icon.py` | Génère l'icône et le logo (Pillow) |

Architecture audio : la sortie vers le câble virtuel sert d'horloge maître et appelle `MixerCore.Render`, qui produit en un seul passage le bus « micro virtuel » et le bus « casque ». Le micro et le casque sont reliés par des tampons anti-gigue à latence bornée. Sans câble virtuel, c'est le casque qui pilote le mixage.
