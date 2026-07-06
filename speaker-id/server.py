import io
import logging

import torch
import torchaudio
from flask import Flask, request, jsonify
from speechbrain.inference.speaker import EncoderClassifier

logging.basicConfig(level=logging.INFO, format="%(asctime)s [speaker-id] %(message)s")
log = logging.getLogger("speaker-id")

app = Flask(__name__)

log.info("Loading speaker embedding model...")
classifier = EncoderClassifier.from_hparams(
    source="speechbrain/spkrec-ecapa-voxceleb",
    savedir="/models/spkrec-ecapa-voxceleb",
)
log.info("Model loaded")

MIN_RMS = 0.01  # below this, the clip is mostly silence/noise/too-far-from-mic
MIN_DURATION_S = 0.8


def trim_silence(waveform, sample_rate):
    # Trims leading silence, then does the same in reverse to trim trailing
    # silence too, so the embedding focuses on actual speech instead of
    # whatever quiet padding/background noise surrounds it.
    try:
        trimmed = torchaudio.functional.vad(waveform, sample_rate=sample_rate)
        reversed_wave = torch.flip(trimmed, dims=[1])
        trimmed = torchaudio.functional.vad(reversed_wave, sample_rate=sample_rate)
        trimmed = torch.flip(trimmed, dims=[1])
        if trimmed.shape[1] < sample_rate * 0.3:  # trimmed almost everything away — not trustworthy
            return waveform
        return trimmed
    except Exception:
        return waveform


@app.route("/health")
def health():
    return jsonify(status="ok")


@app.route("/embed", methods=["POST"])
def embed():
    file = request.files.get("audio")
    if not file:
        return jsonify(error="audio file is required (multipart field 'audio')"), 400

    try:
        audio_bytes = file.read()
        waveform, sr = torchaudio.load(io.BytesIO(audio_bytes))

        if waveform.shape[0] > 1:
            waveform = waveform.mean(dim=0, keepdim=True)
        if sr != 16000:
            waveform = torchaudio.functional.resample(waveform, sr, 16000)
            sr = 16000

        waveform = trim_silence(waveform, sr)

        duration_s = waveform.shape[1] / sr
        rms = waveform.pow(2).mean().sqrt().item()
        low_confidence = rms < MIN_RMS or duration_s < MIN_DURATION_S

        embedding = classifier.encode_batch(waveform)
        vec = embedding.squeeze().tolist()

        return jsonify(embedding=vec, dims=len(vec), rms=rms, duration=duration_s, low_confidence=low_confidence)
    except Exception as e:
        log.error("embedding failed: %s", e)
        return jsonify(error=str(e)), 500


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=5003, threaded=True)
