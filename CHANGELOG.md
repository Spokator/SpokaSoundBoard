# Journal des versions

## 2.14.0

Parlez, une autre voix le dit pour vous.

- **Parler avec la voix choisie** (Synthèse vocale) : parlez normalement dans votre micro. Vos phrases sont écrites sur votre PC, puis dites par la voix de synthèse choisie, dans le jeu et sur Discord, environ une seconde après la fin de chaque phrase. Votre vraie voix ne passe plus tant que c'est activé. Compatible avec le mode radio (seulement quand vous maintenez la touche) et avec un raccourci global ou un bouton de la télécommande pour l'activer en pleine partie.
- **Reconnaissance vocale hors ligne** : NVIDIA Canary (licence CC BY 4.0), à télécharger une fois (208 Mo). Elle comprend le français, l'anglais, l'espagnol et l'allemand, avec la ponctuation. Rien n'est envoyé sur Internet. Avec des enceintes plutôt qu'un casque, la voix de synthèse réentendue par le micro est reconnue et ignorée.
- **Correction, choix de la voix** : la liste des voix ne montrait que « Voix naturelles » et « Voix Windows », sans aucune voix à choisir. Les voix s'affichent maintenant en cartes, par famille, chacune avec ▶ pour l'écouter dans le casque.
- **Voix naturelles décrites** : chaque voix a un court portrait (« Médium, posée », « Très grave », « Claire, rapide »…), mesuré sur des phrases en français. Un lien mène aux réglages de Windows pour ajouter des voix Windows.

## 2.13.0

Vos manettes, joysticks et HOTAS deviennent des raccourcis.

- **Boutons de manette comme raccourcis** : tous les boutons et les chapeaux (POV) d'un HOTAS, d'un joystick, d'un palonnier ou d'une manette de jeu peuvent lancer un son de la soundboard ou n'importe quel raccourci global (mode radio compris). Appuyez simplement sur le bouton quand l'application vous le demande. Ils marchent en pleine partie, sans pilote à installer, et le jeu reçoit toujours le bouton.
- **Plusieurs appareils** : le même numéro de bouton sur le manche et sur la manette des gaz donne deux raccourcis différents. Le raccourci affiche le nom de l'appareil (« T.16000M · Bouton 3 ») et reste valable si vous changez de port USB. Les diagonales d'un chapeau comptent pour les deux directions.
- **Volume de la musique au raccourci** : « Musique plus fort » et « Musique moins fort » (Configuration → Raccourcis globaux) changent le volume de 5 % pour vous, le jeu et Discord, avec le volume affiché. Maintenez la touche ou le bouton pour aller plus vite.
- **Sûr en jeu** : une manette débranchée pendant que vous parlez en mode radio coupe bien le micro, rien ne reste bloqué.
- **Télécommande** : boutons « Musique plus fort » et « Musique moins fort » pour le deck du téléphone, et les actions `/api/action/music-volume-up` et `music-volume-down` pour un Stream Deck.

## 2.12.1

- **Correction importante, lecture de la musique** : depuis la 2.9, une musique au format m4a (le format des musiques téléchargées) lancée par un double-clic ne jouait que 0,4 seconde avant de passer à la suivante. Elle joue de nouveau normalement. La cause : le décodeur de Windows (Media Foundation) refusait d'être utilisé en arrière-plan après une première lecture faite par la fenêtre de l'application. Toute la lecture se fait maintenant en arrière-plan.
- **Diagnostic** : si un fichier ne peut plus être décodé, l'erreur est notée dans le journal (`error.log`), et donc dans le rapport à copier pour signaler un problème.

## 2.12.0

Vos réglages suivent le jeu.

- **Profils par jeu** (Configuration → Profils par jeu) : associez un jeu à ses réglages (effet de voix, onglet de sons, effets, mode radio, anti-bruit, musique dans le micro). Quand le jeu se lance, SpokaSoundBoard passe dessus tout seul, avec un message pour vous prévenir. Chaque réglage peut aussi rester « Ne pas changer ».
- **Retour intelligent** : quelques secondes après la fermeture du jeu, vos réglages d'avant reviennent, y compris une voix réglée à la main. Ce que vous avez changé vous-même pendant la partie est gardé. Un alt-tab rapide ou un jeu qui redémarre ne fait pas clignoter les réglages.
- **Simple à ajouter** : « Ajouter un jeu ouvert… » liste les programmes lancés, et « Choisir le programme du jeu… » permet de le prendre directement sur le disque. Un profil peut ne s'activer que quand le jeu est au premier plan.
- **Télécommande** : le profil actif s'affiche sous le titre, sur le téléphone.

## 2.11.0

Une voix propre, même dans une pièce bruyante.

- **Suppression du bruit** (Voix & effets → Micro) : le clavier, le ventilateur, le souffle et les bruits de fond disparaissent avant les effets, dans le jeu, sur Discord et dans vos enregistrements. Votre voix, elle, reste intacte. Elle fonctionne hors ligne avec RNNoise, une petite intelligence artificielle faite pour la voix (Xiph.Org, licence BSD).
- **Mesuré sur une vraie voix** mêlée à un ventilateur, un clavier et un souffle : pendant les silences, le bruit baisse de 23 dB ; pendant que vous parlez, la voix ressort 7 dB au-dessus du bruit restant, sans être déformée. Elle ne coûte qu'environ 2 % d'un cœur du processeur et ajoute 30 ms de délai.
- **Intensité réglable** : en dessous de 100 %, un peu du son d'origine est gardé, parfaitement aligné avec la voix nettoyée (pas d'effet métallique).
- **Télécommande** : un bouton « Anti-bruit » dans l'onglet Voix du téléphone, et l'action `/api/action/noise` pour un Stream Deck.

## 2.10.0

Une bibliothèque qui tient la charge, et tout SpokaSoundBoard dans un seul fichier.

- **Sauvegarde complète** (Configuration → Sauvegarde complète) : un fichier `.spokabackup` contient vos réglages, votre soundboard avec ses fichiers son, vos effets de voix, raccourcis, deck de télécommande, votre bibliothèque, vos playlists et votre historique. Vous pouvez aussi y inclure les fichiers de musique. Idéal pour changer de PC ou passer de la version installeur à celle du Microsoft Store.
- **Restauration sûre** : « Restaurer une sauvegarde… » montre ce que contient le fichier, garde une copie de l'état actuel dans le dossier des réglages (« Sauvegardes »), puis redémarre l'application. Les sons sont remis à leur place, ou à côté des réglages si leur ancien dossier n'existe pas sur ce PC. Aucun fichier audio n'est supprimé.
- **Import de playlists bien plus rapide** : ajouter 2 000 morceaux à une bibliothèque de 5 000 prend 0,1 seconde au lieu de 12 secondes, pendant lesquelles l'application était figée.
- **Enregistrement sans à-coups** : la bibliothèque s'enregistre en arrière-plan. Pour 5 000 morceaux et 10 000 écoutes, l'interface est occupée 2 ms au lieu de 28 ms à chaque enregistrement, et rien n'est réécrit si rien n'a changé.
- **Historique à part** : les écoutes vont dans leur propre fichier (`history.jsonl`), où elles s'ajoutent à la fin au lieu de tout réécrire. `library.json` est deux fois plus léger. La conversion se fait toute seule au premier lancement.

## 2.9.0

Un son plus solide et plus réactif.

- **Sons instantanés** : les sons de la soundboard sont préparés en mémoire dès le lancement. Une touche lance le son au bloc audio suivant : 0,1 ms en moyenne au lieu d'environ 13 ms, sans jamais lire le disque. Les sons de plus de 30 secondes sont lus à l'avance par petits morceaux.
- **Plus de coupure à cause du disque** : la musique est décodée deux secondes à l'avance, en arrière-plan. Un disque lent, l'antivirus ou un fichier OneDrive pas encore téléchargé ne peuvent plus couper le micro, ni votre voix au passage. Même chose quand vous avancez dans un morceau.
- **Moins de travail pour le fil audio** : le calcul de chaque bloc de son prend en moyenne 18 fois moins de temps qu'avant (0,05 ms au lieu de 0,9 ms). Le pire bloc mesuré passe de 21 ms, assez long pour s'entendre, à 3 ms.
- **Santé audio** : Configuration → Aide et signalement indique maintenant la charge du calcul du son, les blocs en retard, les moments où le PC était trop occupé et les retards de lecture du disque. Un voyant « Coupures audio récentes » s'affiche sous l'état de l'audio quand quelque chose a pu s'entendre dans la dernière minute. Ces chiffres sont aussi dans le rapport à copier pour signaler un problème.

## 2.8.1

- **Mode radio plus sûr** : si Windows avale le relâchement de la touche « parler » (verrouillage du PC, fenêtre d'autorisation administrateur, Ctrl+Alt+Suppr…), le micro ne reste plus ouvert : la touche est relâchée d'elle-même moins d'une seconde plus tard. Le même problème pouvait aussi faire ignorer l'appui suivant sur un raccourci de son : corrigé.
- **Raccourcis pendant la saisie** : quand vous tapez dans l'application (recherche, nom d'un son), seules les touches qui écrivent restent pour le texte. F1 à F24, les boutons de souris et les touches multimédia déclenchent toujours leurs sons et le mode radio. Au lancement, le curseur dans la recherche bloquait jusqu'ici la touche radio.
- **Télécommande** : un nom long comme « Applaudissements » n'est plus coupé au milieu du mot, le texte rétrécit pour tenir dans le bouton.
- **Microsoft Store** : la fiche présente maintenant la télécommande depuis le téléphone.

## 2.8.0

La télécommande fait peau neuve.

- **Trois thèmes au choix** sur le téléphone (bouton ⚙ → Thème) : **Nuit**, des boutons en dégradé de couleur sous une vitre brillante ; **Coucher de soleil**, des boutons en verre sur un fond violet, rose et orange ; **Océan**, du verre fumé sur des lueurs bleu turquoise. Le choix est gardé sur chaque téléphone.
- **De vraies icônes** partout à la place des emojis : nettes, identiques sur iPhone et Android, et intégrées à l'application (elles s'affichent même sans Internet).
- **Une icône pour chaque son** : elle est choisie automatiquement d'après son nom (un visage qui rit pour « Rire du public », une bombe pour « Explosion », une sirène, une corne de brume…), et vous pouvez en choisir une autre parmi plus de cinquante dans l'éditeur du son, sur le PC (« Icône sur la télécommande »). Elle voyage avec les packs.
- **Plus clair d'un coup d'œil** : l'état du micro et l'effet de voix s'affichent sous le titre, et un mini-lecteur en bas montre la musique en cours avec lecture / pause et suivant, à portée de pouce.

## 2.7.0

Votre téléphone devient un Stream Deck.

- **Mon deck** : composez votre page de boutons favoris : sons, effets de voix, phrases à dire dans le micro et actions (tout arrêter, micro, effets, mode radio, « parler », musique, blind test). « Remplir automatiquement » pour démarrer en un geste, « Modifier » pour ajouter, ranger (◀ ▶) ou retirer, appui long sur un son ou un effet pour l'épingler. Le deck est enregistré sur le PC : il est le même sur tous vos téléphones.
- **État en direct** : les sons en cours, le micro, l'effet de voix actif, la musique et le blind test se mettent à jour instantanément sur le téléphone, sans rafraîchir.
- **Voix** : un grand bouton « Maintenir pour parler » (mode radio), qui relâche tout seul si le téléphone perd le Wi-Fi ; micro, effets et mode radio en un appui ; tous les effets de voix ; et « Dire dans le micro » pour taper une phrase que la synthèse vocale prononce, avec vos phrases rapides et récentes.
- **Musique** : pochette, titre, progression, précédent / lecture / suivant, « musique dans le micro » et les volumes (musique, sons, envoi dans le micro, casque).
- **Blind test** : pendant une partie, l'onglet Blind test affiche la réponse sur votre téléphone (et pas à l'écran partagé), les boutons Révéler, Indice, Pause, Rejouer, Passer, Manche suivante, et les points de chaque joueur (Titre +2, Artiste +1) d'un appui.
- **Pensé pour le téléphone** : grands boutons, onglets en bas à portée de pouce, « Stop » toujours visible, vibration au toucher (Android), taille des boutons réglable, affichage paysage façon Stream Deck, et ajout à l'écran d'accueil pour l'ouvrir comme une application.
- **Sur le PC** : un message signale chaque téléphone qui se connecte, et Configuration → Télécommande liste les appareils connectés. L'API pour Stream Deck, Touch Portal et OBS gagne les effets de voix, les phrases, les volumes, le mode radio et le blind test.

## 2.6.0

Pilotez SpokaSoundBoard à distance.

- **Télécommande** (Configuration → Télécommande, désactivée par défaut) : déclenchez vos sons, arrêtez-les, coupez le micro, changez d'effet de voix ou de musique depuis un Stream Deck, Touch Portal, un script OBS ou un simple lien. Chaque adresse contient un code d'accès secret, et une adresse qui se trompe de code plusieurs fois est bloquée.
- **Depuis votre téléphone** : activez « Depuis mon téléphone », scannez le QR code avec un téléphone sur le même Wi-Fi, et toute votre soundboard s'affiche (avec ses onglets) : un appui joue le son dans votre micro. Le micro, les effets, l'effet de voix et la musique se pilotent aussi. Aucune application à installer.

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
