Attack page audio assets
========================

firstblood.mp3
--------------
First-blood stinger played by the /games/{id}/attack page when a team
earns a first blood.

Source: https://github.com/0x4m4/first-strike-alert
File:   static/sounds/blood.mp3

incoming.mp3
------------
"Incoming attack" build-up alarm played during the ~5s telegraph that
precedes the first-blood slam.

Source:  Mixkit — "Space shooter alarm" (https://mixkit.co/free-sound-effects/space-shooter-alarm/)
License: Mixkit Free License (free for commercial use, no attribution required;
         redistribution as a standalone sound is not permitted — embedded use only)
Editing: trimmed to 5s, fade-in + crescendo into the slam + fade-out, level-matched
         to firstblood.mp3 (ffmpeg).

To replace either, drop an mp3 with the same filename here and rebuild the
frontend — no code changes required.
