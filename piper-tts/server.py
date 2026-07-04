import os
import subprocess
import tempfile
import time
import logging

from flask import Flask, request, Response, jsonify

logging.basicConfig(level=logging.INFO, format="%(asctime)s [piper-tts] %(message)s")
log = logging.getLogger("piper-tts")

app = Flask(__name__)

PIPER_BIN = "/opt/piper/piper"
ESPEAK_DATA = "/opt/piper/espeak-ng-data"
MODEL_PATH = "/models/ru_RU-irina-medium.onnx"

env = os.environ.copy()
env["LD_LIBRARY_PATH"] = "/opt/piper"


@app.route("/health")
def health():
    return jsonify(status="ok")


@app.route("/synthesize", methods=["POST"])
def synthesize():
    data = request.get_json(silent=True) or {}
    text = (data.get("text") or "").strip()
    if not text:
        return jsonify(error="text is required"), 400

    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
        out_path = tmp.name

    try:
        start = time.monotonic()
        result = subprocess.run(
            [PIPER_BIN, "--model", MODEL_PATH, "--espeak_data", ESPEAK_DATA, "--output_file", out_path],
            input=text.encode("utf-8"),
            capture_output=True,
            env=env,
            timeout=60,
        )
        elapsed_ms = int((time.monotonic() - start) * 1000)

        if result.returncode != 0:
            log.error("piper failed: %s", result.stderr.decode("utf-8", "replace"))
            return jsonify(error="synthesis failed"), 500

        with open(out_path, "rb") as f:
            audio_bytes = f.read()

        log.info("synthesized %d chars -> %d bytes in %dms", len(text), len(audio_bytes), elapsed_ms)
        return Response(audio_bytes, mimetype="audio/wav")
    finally:
        try:
            os.unlink(out_path)
        except OSError:
            pass


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=5001)
