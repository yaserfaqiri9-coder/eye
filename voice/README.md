# Patient-call voice pack

`amir/` holds the 154 MP3 clips the call system plays: numbers 1 to 100, the seven sections and rooms 1 to 20, each in the normal and "again" form. They were made with `deploy/windows/build-voice-pack-piper.py` and the Piper voice `fa_IR-amir-medium` in its own pronunciation, only a little slower (phrases in `deploy/windows/voice-phrases.json`).

amir drops the k of «یک», so the eleven clips that say it come from `ganji/` instead: `number-0001`, `-0021`, `-0031`, `-0041`, `-0051`, `-0061`, `-0071`, `-0081`, `-0091`, `to-room-01` and `again-room-01`. After rebuilding `amir/`, copy these from `ganji/` again.

Install on the host: run `Import-AlShifa-Voice.exe` and pick this folder, or build the installer with `build-installer.ps1 -VoicePack voice\amir`.

**Licence:** the amir dataset is labelled CC0, but the voice was fine-tuned from a voice trained on Lessac data whose licence allows research use only. Using these clips in a working hospital carries that risk; the hospital accepted it. Check the licence before reusing them elsewhere.
