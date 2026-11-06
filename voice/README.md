# Patient-call voice pack

`ganji/` holds the 1053 MP3 clips the call system plays: numbers 1 to 999, the seven sections and rooms 1 to 20, each in the normal and "again" form. They were made with `deploy/windows/build-voice-pack-piper.py` and the Piper voice `fa_IR-ganji-medium` (phrases in `deploy/windows/voice-phrases.json`).

Install on the host: run `Import-AlShifa-Voice.exe` and pick this folder, or build the installer with `build-installer.ps1 -VoicePack voice\ganji`.

**Licence:** the ganji dataset is labelled CC0, but the voice was fine-tuned from Piper's amir voice, which was fine-tuned from a voice trained on Lessac data whose licence allows research use only. Using these clips in a working hospital carries that risk; the hospital accepted it. Check the licence before reusing them elsewhere.
