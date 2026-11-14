# SpokaVoiceEngine

Moteur de changement de voix en temps réel de SpokaSoundBoard : votre voix, avec votre intonation, prend le timbre d'une
autre voix. Il exécute le modèle [Seed-VC](https://github.com/Plachtaa/seed-vc) (version temps réel « xlsr-tiny »)
sur la carte graphique avec ONNX Runtime et DirectML (NVIDIA, AMD ou Intel, DirectX 12).

## Licence

Ce dossier est sous **licence GPL-3.0-or-later** (voir [LICENSE](LICENSE)), comme le modèle Seed-VC qu'il exécute.
C'est un programme séparé : SpokaSoundBoard (licence MIT) le lance et échange l'audio avec lui par son entrée et sa
sortie standard, sans partager de code.

Les modèles, téléchargés par SpokaSoundBoard au premier usage, viennent de :

| Fichier | Origine | Licence |
| --- | --- | --- |
| `content.onnx` | [XLS-R 300M](https://huggingface.co/facebook/wav2vec2-xls-r-300m) (Meta), 12 premières couches | Apache-2.0 |
| `regulator.onnx`, `dit.onnx` | [Seed-VC](https://huggingface.co/Plachta/Seed-VC) `DiT_uvit_tat_xlsr_ema.pth` | GPL-3.0 |
| `f0.onnx`, `hift.onnx`, `source.json` | Vocodeur HiFT de [CosyVoice-300M](https://huggingface.co/FunAudioLLM/CosyVoice-300M) | Apache-2.0 |
| `campplus.onnx`, `fbank.bin`, `melbasis.bin` (création de voix) | CAMPPlus de [3D-Speaker](https://github.com/modelscope/3D-Speaker) (`funasr/campplus`, utilisé par Seed-VC), bancs de filtres Kaldi et librosa | Apache-2.0 |
| `voice_*.bin` | Empreintes des 10 voix naturelles de SpokaSoundBoard (Supertonic 3, Supertone) | [OpenRAIL-M](https://huggingface.co/Supertone/supertonic-3/blob/main/LICENSE) : interdiction d'imiter une personne sans son accord, de harceler ou de diffamer |

Les poids sont stockés en float16 et calculés en float32. Les scripts de [tools](tools) les reproduisent à
l'identique depuis les modèles d'origine :

1. `export_onnx.py` exporte les graphes et vérifie chacun contre PyTorch.
2. `make_voices.py` calcule les empreintes de voix.
3. `compress_weights.py` stocke les poids en float16.
4. `export_creator.py` exporte ce qui sert à créer une voix (« Mes voix »).
5. `ort_stream.py` est l'implémentation de référence de la boucle temps réel, dont `VoiceConverter.cs` est la
   traduction.

## Protocole

Chaque message, dans les deux sens : `uint32 type`, `uint32 longueur`, puis les données (little endian).

| Type | Sens | Contenu |
| --- | --- | --- |
| 1 Init | → moteur | JSON `{models, voice, blockMs, steps, promptSeconds, contentContextSeconds, autoBlock}` |
| 2 Audio | → moteur | float32, 16 kHz mono |
| 3 Voice | → moteur | JSON `{voice}` |
| 4 Reset / 5 Quit | → moteur | vide |
| 101 Ready | ← moteur | JSON : carte graphique, taille de bloc, temps de calcul mesuré |
| 102 Audio | ← moteur | float32, 22,05 kHz mono |
| 103 Stats | ← moteur | JSON : temps de calcul, charge, changement de taille de bloc |
| 104 Error | ← moteur | JSON `{fatal, code, message}` |

Mode fichier : `SpokaVoiceEngine --convert <modèles> <voix.bin> <entrée 16 kHz.wav> <sortie.wav> [bloc ms] [graine] [contexte s] [prompt s] [étapes]`.

Création d'une voix (« Mes voix ») : `SpokaVoiceEngine --imprint <modèles> <outils de création> <parole 22,05 kHz.wav> <parole 16 kHz.wav> <voix.bin>` calcule sur le processeur l'empreinte d'une voix (timbre CAMPPlus moyenné sur des fenêtres de 3 s, et les 3 premières secondes comme exemple) et écrit un fichier de voix SVCV ; il affiche un résumé JSON.
