import io
import logging

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

        embedding = classifier.encode_batch(waveform)
        vec = embedding.squeeze().tolist()

        return jsonify(embedding=vec, dims=len(vec))
    except Exception as e:
        log.error("embedding failed: %s", e)
        return jsonify(error=str(e)), 500


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=5003, threaded=True)
