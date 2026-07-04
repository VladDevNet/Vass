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
SAMPLE_RATE = 22050  # matches ru_RU-irina-medium.onnx.json audio.sample_rate

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


@app.route("/synthesize_stream", methods=["POST"])
def synthesize_stream():
    data = request.get_json(silent=True) or {}
    text = (data.get("text") or "").strip()
    if not text:
        return jsonify(error="text is required"), 400

    def generate():
        start = time.monotonic()
        proc = subprocess.Popen(
            [PIPER_BIN, "--model", MODEL_PATH, "--espeak_data", ESPEAK_DATA, "--output-raw"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env=env,
        )
        proc.stdin.write(text.encode("utf-8"))
        proc.stdin.close()

        total_bytes = 0
        first_chunk_ms = None
        try:
            while True:
                chunk = proc.stdout.read(4096)
                if not chunk:
                    break
                if first_chunk_ms is None:
                    first_chunk_ms = int((time.monotonic() - start) * 1000)
                total_bytes += len(chunk)
                yield chunk
        finally:
            proc.stdout.close()
            try:
                proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                proc.kill()
            stderr = proc.stderr.read()
            proc.stderr.close()
            elapsed_ms = int((time.monotonic() - start) * 1000)
            if proc.returncode != 0:
                log.error("piper stream failed: %s", stderr.decode("utf-8", "replace"))
            else:
                log.info(
                    "streamed %d chars -> %d bytes, first chunk %sms, total %dms",
                    len(text), total_bytes, first_chunk_ms, elapsed_ms,
                )

    return Response(generate(), mimetype=f"audio/L16;rate={SAMPLE_RATE};channels=1")


if __name__ == "__main__":
    app.run(host="0.0.0.0", port=5001, threaded=True)
