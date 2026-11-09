"""Builds the voice pack with a free local Piper voice, as an alternative to Azure in build-voice-pack.ps1.

    pip install piper-tts imageio-ffmpeg
    build-voice-pack.ps1 -Mode Export -ExportFile clips.csv
    python build-voice-pack-piper.py fa_IR-amir-medium.onnx clips.csv E:\\voice
    build-voice-pack.ps1 -Mode Check -VoiceDir E:\\voice

The hospital chose amir as it is: its own reading is the clearest for Afghan patients. Do not respell words or change
its voice settings; only the pace is slowed. Licence: check the voice before use. amir was fine-tuned from a voice
trained on Lessac data, whose licence is for research only. See DEPLOYMENT-WINDOWS.md.
"""
import csv
import re
import subprocess
import sys
import unicodedata
from pathlib import Path

import imageio_ffmpeg
from piper import PiperVoice, SynthesisConfig

# Above 1 speaks slower. The hospital asked for amir a little slower than its own pace.
LENGTH_SCALE = 1.2
# Silence after each clip, so the last syllable is never cut off by the next clip.
TAIL_SECONDS = 0.3

# eSpeak phonemes for words it reads wrongly, found on the amir voice.
FIXES = {
    "nˈa": "nˈoh",  # نه (9)
    "nˈasˈad": "nˈohsˌad",  # نه‌صد (900)
    "nazˈadeː": "nˈozdah",  # نزده (19)
    "dˈuːsd": "dˈuːsˌad",  # دوصد (200)
    "rafrkˈeʃan": "rˈefrakʃˌen",  # رفرکشن
}
WRONG = re.compile(r"(?<![^\s,.;:!?])(" + "|".join(re.escape(k) for k in sorted(FIXES, key=len, reverse=True)) + r")(?![^\s,.;:!?])")


def main(model: Path, clips_csv: Path, out_dir: Path) -> None:
    ffmpeg = imageio_ffmpeg.get_ffmpeg_exe()
    voice = PiperVoice.load(str(model))
    phonemize = voice.phonemize
    voice.phonemize = lambda text: [
        list(unicodedata.normalize("NFD", WRONG.sub(lambda m: FIXES[m.group(1)], "".join(sentence))))
        for sentence in phonemize(text)
    ]

    with clips_csv.open(encoding="utf-8-sig", newline="") as handle:
        clips = list(csv.DictReader(handle))

    out_dir.mkdir(parents=True, exist_ok=True)
    config = SynthesisConfig(length_scale=LENGTH_SCALE)
    for clip in clips:
        audio = b"".join(chunk.audio_int16_bytes for chunk in voice.synthesize(clip["Text"], syn_config=config))
        subprocess.run(
            [ffmpeg, "-y", "-loglevel", "error", "-f", "s16le", "-ar", str(voice.config.sample_rate), "-ac", "1", "-i", "pipe:0",
             "-af", f"apad=pad_dur={TAIL_SECONDS}", "-codec:a", "libmp3lame", "-b:a", "96k", str(out_dir / clip["File"])],
            input=audio,
            check=True,
        )
    print(f"Wrote {len(clips)} clips to {out_dir}")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit("Usage: build-voice-pack-piper.py <voice.onnx> <clips.csv> <output folder>")
    main(Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3]))
