# Politique de confidentialité de SpokaSoundBoard

*Mise à jour le 11 novembre 2026*

Cette politique s'applique à SpokaSoundBoard, dans sa version Microsoft Store comme dans sa version téléchargée depuis GitHub (installeur). Si l'application change sa façon de traiter les données, cette page change aussi, et sa date ci-dessus avec elle.

## En bref

SpokaSoundBoard est une soundboard, un lecteur de musique et un module d'effets de voix qui fonctionne sur votre PC. **Il n'y a ni compte, ni publicité, ni télémétrie, ni statistiques d'utilisation, ni rapport d'erreur envoyé.** Nous n'avons pas de serveur : nous ne recevons et ne conservons aucune donnée vous concernant.

## Le micro

SpokaSoundBoard utilise votre micro pour appliquer les effets de voix et l'envoyer, avec la musique et les sons, dans le micro virtuel que vous choisissez pour Discord ou vos jeux. **Le son est traité en direct, sur votre PC uniquement.** Il n'est ni enregistré, ni envoyé par l'application à nous ou à quiconque. Ce que Discord ou un jeu fait ensuite du son qu'il reçoit dépend de leurs propres règles.

Vous pouvez couper le micro à tout moment dans l'application, ou retirer l'autorisation dans Paramètres Windows → Confidentialité et sécurité → Microphone.

## Ce qui reste sur votre PC

- Vos réglages, votre soundboard, votre bibliothèque de musique, vos playlists, vos effets de voix et vos raccourcis.
- Les sons et musiques que vous importez ou téléchargez, et leur analyse de volume.
- Un journal d'erreurs local (`error.log`), qui n'est jamais envoyé : vous pouvez le joindre vous-même à un signalement si vous le souhaitez.

La version installeur range ces fichiers dans `%AppData%\SpokaSoundBoard`. La version Microsoft Store les range dans le dossier privé que Windows lui réserve, supprimé quand vous la désinstallez.

## Les raccourcis clavier et souris

Pour que vos raccourcis marchent en pleine partie, SpokaSoundBoard observe les touches et les boutons de souris pressés dans Windows. Il compare chaque touche à vos raccourcis puis l'oublie aussitôt : **rien n'est enregistré ni envoyé**, et la touche continue d'arriver normalement au jeu.

## Quand l'application se connecte à Internet

SpokaSoundBoard ne se connecte que pour les fonctions en ligne que vous utilisez, et pour vérifier les mises à jour (version installeur). Voici la liste complète :

- **Recherche et import de liens** : quand vous cherchez un son ou une musique, ou que vous collez un lien, l'application interroge le site concerné, par exemple **YouTube**, **SoundCloud** ou **MyInstants**, et télécharge le fichier choisi. Elle utilise pour cela les outils libres yt-dlp et ffmpeg, livrés avec l'application.
- **Suggestions de recherche** : pendant que vous tapez une recherche de musique, le texte tapé est envoyé au service de suggestions de **Google** pour proposer des titres.
- **Pochettes** : les images des musiques et des résultats de recherche YouTube sont chargées depuis les serveurs d'images de YouTube (**i.ytimg.com**).
- **Autoplay, radio et « Pour vous »** : quand la file d'attente se termine avec l'autoplay activé, ou quand vous lancez une radio ou ouvrez les suggestions, l'application demande à **YouTube** des morceaux proches de ceux que vous écoutez, puis les télécharge pour les jouer. Vous pouvez désactiver l'autoplay dans la page Musique.
- **Voix naturelles** : si vous choisissez de les télécharger, les fichiers des voix sont récupérés une seule fois sur **Hugging Face** (huggingface.co). Elles fonctionnent ensuite hors ligne : le texte que vous faites lire ne quitte jamais votre PC.
- **Paroles** : pour afficher les paroles du morceau en cours, l'application envoie son titre, son artiste et sa durée à **lrclib.net**.
- **Playlists** : si vous importez un lien de playlist **Spotify**, **Deezer** ou **Apple Music**, l'application lit la page publique de cette playlist (ou l'API publique de Deezer) pour en connaître les titres, puis les cherche comme ci-dessus.
- **Mises à jour, version installeur uniquement** : au démarrage, l'application demande à **GitHub** s'il existe une nouvelle version. Vous pouvez le désactiver dans Configuration → Mises à jour. La version Microsoft Store ne le fait jamais : c'est le Store qui la met à jour.
- **Micro virtuel** : les boutons d'installation ouvrent le site officiel de VB-CABLE (**vb-audio.com**) ou de Virtual Audio Cable (**vac.muzychenko.net**). La version installeur peut aussi y télécharger directement l'installeur officiel.

Comme toute connexion, ces requêtes transmettent votre adresse IP au site contacté, avec ce que vous avez cherché ou le lien que vous avez collé. Ces sites appliquent leurs propres politiques de confidentialité. SpokaSoundBoard n'y ajoute aucune information personnelle (nom, e-mail, identifiant).

## Partage et vente

Nous ne collectons aucune donnée : il n'y a donc rien à partager, à vendre ni à transmettre à des tiers.

## Enfants

SpokaSoundBoard ne demande aucune information personnelle et s'utilise sans compte. Si vous avez moins de 15 ans, demandez l'accord d'un parent avant de l'utiliser avec Discord ou des jeux en ligne, qui ont leurs propres règles.

## Vos droits et nous contacter

Comme vos données restent sur votre PC, vous en gardez le contrôle : désinstaller l'application, ou supprimer son dossier de données, efface tout.

Pour toute question, écrivez-nous sur [la page du projet](https://github.com/Spokator/SpokaSoundBoard/issues), sans y mettre d'information personnelle. Si vous estimez que vos droits ne sont pas respectés, vous pouvez saisir la [CNIL](https://www.cnil.fr).

## Qui s'en occupe

SpokaSoundBoard est un logiciel publié par Spokator, dont le code est [consultable sur GitHub](https://github.com/Spokator/SpokaSoundBoard).
