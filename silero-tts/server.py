import io
import logging
import time
import wave

import numpy as np
import torch
from flask import Flask, Response, jsonify, request

logging.basicConfig(level=logging.INFO, format="%(asctime)s [silero-tts] %(message)s")
log = logging.getLogger("silero-tts")

app = Flask(__name__)

MODEL_PATH = "/models/v4_ru.pt"
SAMPLE_RATE = 48000
DEFAULT_SPEAKER = "xenia"  # female voice; alternatives: baya, kseniya, aidar (male), eugene (male)

torch.set_num_threads(4)
device = torch.device("cpu")
model = torch.package.PackageImporter(MODEL_PATH).load_pickle("tts_models", "model")
model.to(device)
log.info("Silero v4_ru model loaded")


def synthesize_wav(text, speaker):
    audio = model.apply_tts(text=text, speaker=speaker, sample_rate=SAMPLE_RATE)
    pcm16 = (audio.numpy() * 32767).astype(np.int16)
    buf = io.BytesIO()
    with wave.open(buf, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(SAMPLE_RATE)
        wf.writeframes(pcm16.tobytes())
    return buf.getvalue()


@app.route("/health")
def health():
    return jsonify(status="ok")


@app.route("/synthesize", methods=["POST"])
def synthesize():
    data = request.get_json(silent=True) or {}
    text = (data.get("text") or "").strip()
    speaker = data.get("speaker") or DEFAULT_SPEAKER
    if not text:
        return jsonify(error="text is required"), 400

    try:
        start = time.monotonic()
        wav_bytes = synthesize_wav(text, speaker)
        elapsed_ms = int((time.monotonic() - start) * 1000)
        log.info("synthesized %d chars -> %d bytes in %dms (speaker=%s)", len(text), len(wav_bytes), elapsed_ms, speaker)
        return Response(wav_bytes, mimetype="audio/wav")
    except Exception as e:
        log.error("synthesis failed: %s", e)
        return jsonify(error=str(e)), 500


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=5002, threaded=True)
