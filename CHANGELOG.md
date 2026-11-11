# Journal des versions

## 2.5.1

- **Outils vérifiés** : yt-dlp et le convertisseur audio ffmpeg, téléchargés à la première utilisation dans la version installeur, sont comparés à l'empreinte SHA-256 publiée par leurs auteurs. Un fichier qui ne correspond pas est supprimé sans jamais être lancé. Ceux livrés avec l'installeur et la version Microsoft Store sont vérifiés de la même façon.
- **Preset renommé** : « Dark Vador » s'appelle maintenant « Seigneur noir » (même voix). Si c'était votre effet actif, il le reste.

## 2.5.0

Blind test sur un thème, et une application plus facile à utiliser au clavier.

- **Blind test sur un thème** : nouvelle source « Un thème » : tapez « années 80 », « rap français », « Disney »… et les morceaux sont cherchés sur YouTube, sans rien avoir dans votre bibliothèque. Les compilations, remix, reprises et versions live sont écartés.
- **Indices** : pendant une manche, « Indice » affiche la forme du titre (V••••• V•••••), puis ses premières lettres, puis les initiales de l'artiste.
- **Aide et signalement** : dans Configuration, « Copier le rapport de diagnostic » (version, périphériques, compteurs audio, dernières erreurs) à coller dans un signalement, « Signaler un problème » et « Ouvrir le dossier des réglages ». Les compteurs audio se sélectionnent et se copient.
- **Navigation au clavier** : un contour indique l'élément sélectionné quand vous naviguez avec Tab.
- **Couleurs claires lisibles** : avec une couleur d'accent claire (or, vert, cyan), le texte des boutons passe en foncé pour rester lisible.

## 2.4.0

La musique se range et se corrige.

- **Trier les listes** : « Toutes les musiques », les favoris et les playlists se trient par titre, artiste, durée ou nombre d'écoutes (menu « Trier par », mémorisé). L'ordre alphabétique suit le français : accents et majuscules ne changent rien.
- **Mauvaise version ? Choisissez la bonne** : clic droit sur un morceau → « Infos et autre version… ». Le panneau « Morceau » propose les autres vidéos trouvées (reprises, live, clip officiel…), écoutables au casque avant de choisir. Le bon fichier est téléchargé à la place de l'ancien, qui est supprimé.
- **Modifier le titre et l'artiste** d'un morceau dans ce même panneau, y compris pour vos fichiers.
- **Mettre à jour une playlist importée** : le bouton « Mettre à jour » d'une playlist venue de Spotify, Deezer, Apple Music, YouTube ou SoundCloud ajoute ses nouveaux morceaux et retire ceux qui n'y sont plus (ils restent dans votre bibliothèque).
- **Stockage** : la page Configuration montre la place prise par les musiques, les sons et les voix naturelles, et « Libérer l'espace des musiques » supprime les fichiers des morceaux téléchargés qui ne sont ni en favori, ni dans une playlist, ni dans la file (ils restent dans la bibliothèque et se retéléchargent si vous les écoutez).
- **Suivant en fin de file** : sans répétition, « Suivant » sur le dernier morceau continue avec l'autoplay au lieu de revenir au début.
- **Clavier** : la touche Menu ou Maj+F10 ouvre le menu du morceau sélectionné dans une liste.
- **Correction** : « Ne plus proposer ce morceau » n'affiche plus de confirmation pour un fichier de votre PC (l'autoplay ne le propose de toute façon jamais).

## 2.3.0

La soundboard s'organise.

- **Catégories en onglets** : rangez vos sons dans des onglets (« Réactions », « Bruitages »…) au-dessus de la soundboard. « Tous » les montre tous. Créez un onglet avec « + Catégorie », renommez-le ou supprimez-le d'un clic droit (les sons restent), et glissez un son sur un onglet pour l'y ranger. Les nouveaux sons vont dans l'onglet affiché.
- **Réorganiser par glisser-déposer** : déplacez un son sur un autre pour changer l'ordre.
- **Options de lecture par son** : quand on rappuie pendant la lecture, l'arrêter, le relancer depuis le début, ou le superposer. « Arrêter les autres sons » coupe tout ce qui joue quand il démarre.
- **Moi seulement** : un son peut être joué uniquement dans votre casque, sans que personne d'autre l'entende (repérable à l'icône de casque sur sa tuile).
- **Écouter avant de diffuser** : dans l'éditeur d'un son, « Écouter » le joue dans votre casque seulement, « Diffuser » l'envoie dans le micro. Aussi dans le clic droit : « Écouter dans mon casque ».
- **Enregistrer un son avec le micro** : bouton « Enregistrer » en haut de la soundboard, jusqu'à 2 minutes. Le silence au début et à la fin est coupé automatiquement.
- **Raccourcis en double signalés** : si la touche choisie sert déjà à autre chose, un message le dit tout de suite.
- **Périphériques suivis automatiquement** : brancher ou débrancher un casque, un micro ou un câble virtuel est détecté sans cliquer sur « Actualiser ». Pendant qu'un périphérique est débranché, le son passe vraiment par celui de secours.
- **Packs** : les catégories et options de lecture sont incluses dans les `.spokapack`. Un pack plus ancien arrive dans un onglet à son nom, et réimporter le même pack ne double plus ses sons.

## 2.2.2

Corrections issues d'une relecture complète de l'application.

- **Raccourcis en jeu** : un son sur F1 se déclenche aussi quand vous maintenez Maj, Ctrl ou Alt (pour courir ou vous accroupir). Si vous avez aussi lié « Maj + F1 », c'est celui-là qui joue. Taper du texte dans SpokaSoundBoard (nom d'un son, recherche) ne déclenche plus les raccourcis d'une seule touche.
- **Périphériques débranchés** : si votre casque ou un câble disparaît un instant, l'application utilise un autre périphérique en attendant, puis reprend le vôtre tout seul dès qu'il revient, au lieu de l'oublier. Choisir « aucun » pour le casque ou le jeu est maintenant mémorisé.
- **Réglages sauvés** : si le fichier de réglages est abîmé, seule la partie illisible est remise par défaut (vos sons et le reste sont gardés), une copie de l'ancien fichier est conservée et un message vous prévient.
- **Soundboard** : arrêter ou remplacer un son ne fait plus de clic (fondu de quelques millisecondes). Deux sons MyInstants qui ont le même titre ne partagent plus le même fichier. « Changer le fichier » remet à zéro l'extrait et le volume calculé pour l'ancien. Un double appui très rapide ne lance plus le son deux fois.
- **Musique** : l'autoplay ne coupe plus le morceau que vous venez de lancer pendant qu'il cherchait la suite. Réimporter une playlist Spotify, Deezer ou Apple Music ne double plus ses morceaux. « Réessayer » sur les paroles interroge vraiment le service à nouveau, et une panne passagère n'est plus prise pour « paroles introuvables ».
- **Blind test** : les effets musique (nightcore, karaoké…) ne s'appliquent plus aux extraits, le bouton « Stop » de la musique ne coupe plus l'extrait, et la position du morceau interrompu est bien retrouvée au redémarrage.
- **Synthèse vocale** : « Enregistrer comme son » garde votre phrase même après un blind test. Le curseur de vitesse s'arrête à 2× pour les voix naturelles (leur maximum), et « Langue lue » n'apparaît que pour elles.
- **Divers** : la couleur d'accent change partout sans redémarrer ; les musiques et sons téléchargés que plus rien n'utilise sont supprimés automatiquement (après 2 jours) pour libérer de la place ; l'application consomme moins quand elle est cachée dans la zone de notification ; le message d'erreur inattendue est en français ; la politique de confidentialité liste tous les sites contactés.

## 2.2.1

Version de fiabilité, sans nouveauté visible.

- **Mises à jour plus sûres** : l'installateur téléchargé est vérifié (taille et empreinte SHA-256 publiées par GitHub) avant d'être lancé. S'il ne correspond pas, il est supprimé et rien n'est installé.
- **Réglages protégés** : une valeur abîmée dans le fichier de réglages (effet de voix, mixage, effets musique, raccourci vide) est remise dans les limites au lieu de couper le son ou d'effacer tous vos réglages. Les presets importés depuis un pack sont vérifiés de la même façon.
- **Soundboard plus fluide** : 24 sons au plus en même temps (le plus ancien s'arrête), et les fichiers terminés sont refermés en arrière-plan, sans gêner le son.
- **Messages d'erreur en français** : les erreurs réseau, de fichier ou de périphérique sont expliquées clairement au lieu d'afficher le message technique en anglais.

## 2.2.0

- **Synthèse vocale dans son propre onglet** : elle a maintenant sa place dans le menu de gauche, juste sous Soundboard, au lieu d'être cachée dans la page Soundboard.
- **« Trouver des sons » toujours visible** : la recherche de sons n'est plus cachée derrière un bouton. Un clic sur son titre la replie sur une seule ligne pour garder toute la place pour vos sons et leurs raccourcis (le choix est mémorisé).
- **Le bon nom de micro, partout** : la page Configuration, le guide Discord et l'assistant affichent le nom exact du micro virtuel à choisir, lu depuis Windows. C'est plus fiable avec VB-CABLE 16 canaux, Voicemeeter ou un câble renommé.

## 2.1.0

- **Voix naturelles** pour la synthèse vocale : 10 voix neuronales (5 femmes, 5 hommes), bien plus réalistes que les voix Windows, en français et dans 30 autres langues. Elles fonctionnent hors ligne après un téléchargement unique de 139 Mo depuis les options de la synthèse vocale, avec aperçu au casque ; les voix Windows restent disponibles. Le blind test annonce les réponses avec la voix choisie.
- **Menu de gauche rétractable** : le bouton ☰ à côté du logo le réduit aux icônes pour laisser plus de place aux pages (le choix est mémorisé).
- **Page Musique adaptée aux petites fenêtres** : le panneau de droite se resserre, puis la bibliothèque passe en icônes quand la place manque. Le panneau (file d'attente, paroles, effets) a maintenant un bouton pour le fermer.
- **Correction** : les bulles d'aide des boutons affichaient des carrés au lieu du texte.

## 2.0.0

Le grand saut de l'onglet **Musique**, qui devient un vrai lecteur, toujours diffusé dans votre micro.

- **Bibliothèque** : accueil (écoutés récemment, ajoutés récemment, les plus écoutés), toutes les musiques, favoris, historique, pochettes et artistes (tags des fichiers, YouTube).
- **Playlists** : autant que vous voulez, réordonnables par glisser-déposer, export M3U. Votre ancienne liste devient « Ma playlist ».
- **Import de playlists** Spotify, Deezer, Apple Music, YouTube et SoundCloud : collez le lien, les morceaux sont téléchargés au moment de les écouter.
- **File d'attente** façon Spotify : lire ensuite, ajouter à la file, aléatoire sans répétition, retour arrière fidèle, doublons, enregistrer la file en playlist, reprise au redémarrage.
- **Fondu enchaîné** réglable (0 à 12 s) ou enchaînement sans blanc, morceau suivant préchargé, transitions sans clic.
- **Recherche** avec suggestions et classement intelligent (version officielle avant les reprises, live et remix), aperçu au casque, « Lire maintenant ».
- **Autoplay et radio** : quand la file se termine, des morceaux similaires s'enchaînent. « Pour vous » propose des titres selon vos écoutes, « Ne plus proposer » affine les suggestions.
- **Paroles synchronisées** (karaoké) : la ligne en cours s'affiche en grand, cliquez sur une ligne pour y aller.
- **Effets musique en direct** : basses, aigus, égaliseur 10 bandes, vitesse (nightcore, vaporwave, slowed + reverb), audio 8D, karaoké (voix retirée), salle de concert, largeur stéréo, 13 presets.
- **Statistiques** : top morceaux et artistes, heures et jours d'écoute, séries, rétrospective de l'année.
- **Touches multimédia** du clavier et carte « en cours de lecture » de Windows, visualiseur dans la barre de lecture.
- **Blind test** : nouvel onglet pour animer un blind test dans Discord ou en jeu. Extraits joués dans votre micro (playlist, favoris, bibliothèque ou historique), compte à rebours, révélation avec pochette et annonce vocale, scores par joueur (titre, artiste, rapidité), podium et classement de tous les temps.
- **Synthèse vocale** : tapez une phrase, elle est dite dans votre micro (vitesse, hauteur, volume), phrases rapides, historique et « Enregistrer comme son » pour lui donner un raccourci.
- **Microsoft Store** : SpokaSoundBoard est prêt à être publié sur le Store (mises à jour par le Store, lancement au démarrage via Windows, ouverture des `.spokapack` en double-clic).

## 1.4.0

- **Plus de micro-coupures** : compensation de la dérive d'horloge entre cartes son (rééchantillonnage adaptatif inaudible). Les coupures d'environ 1 s toutes les 30 s à 2 min sur Discord disparaissent.
- **Diagnostic audio** dans Configuration : compteurs de coupures par sortie et corrections de volume.
- **Personnalisation** : thèmes Sombre / Noir OLED / Clair, couleurs prédéfinies ou personnalisées, zoom de l'interface, taille des tuiles, affichage des vumètres et des raccourcis.
- **Nouveaux effets de voix** : égaliseur 3 bandes, chorus, distorsion / lo-fi, filtre étouffé, bips radio au choix (roger, NASA « Quindar », simple).
- **13 nouveaux presets** : Astronaute (NASA), Pilote de chasse, Téléphone, Vieille radio, Annonce stade / gare, Voix pro, Dark Vador, Démon, Chipmunk, Fantôme, Alien, Sous l'eau, Lo-fi 8 bits.
- **Volume égalisé automatiquement** pour les sons et les musiques.
- **Packs de soundboard** (`.spokapack`) : exportez votre soundboard avec ses sons et envoyez-la à vos amis.
- **Filtre** pour retrouver un son, raccourcis **effet de voix suivant / précédent**.
- **Mises à jour automatiques** depuis GitHub.

## 1.3.0

- Recherche intégrée : YouTube (musique, sons) et MyInstants (bruitages), avec aperçu dans le casque.
- Zone de notification (systray) : la croix réduit l'application, menu d'actions rapides.
- Protection contre l'atténuation de Windows et de Discord.
- Correction des accents dans les titres importés.

## 1.2.0

- Installeur complet (sans droits administrateur) et assistant de première configuration.
- Lancement au démarrage de Windows.

## 1.1.0

- Sorties séparées Jeu / Discord avec le tableau « Qui entend quoi ? ».
- Import par lien (YouTube, SoundCloud, MyInstants, TikTok…), extraits début / fin pour les sons.

## 1.0.0

- Soundboard, lecteur de musique, ducking automatique, effets de voix en temps réel, mode radio, raccourcis globaux.
