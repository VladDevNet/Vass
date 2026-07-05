FROM debian:bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl ca-certificates python3 python3-pip libsndfile1 ffmpeg \
    && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir --break-system-packages flask numpy
RUN pip install --no-cache-dir --break-system-packages torch torchaudio --index-url https://download.pytorch.org/whl/cpu
RUN pip install --no-cache-dir --break-system-packages speechbrain

WORKDIR /app
COPY server.py .

# Pre-download the model at build time so it's baked into the image and the
# first real request isn't slow.
RUN python3 -c "from speechbrain.inference.speaker import EncoderClassifier; EncoderClassifier.from_hparams(source='speechbrain/spkrec-ecapa-voxceleb', savedir='/models/spkrec-ecapa-voxceleb')"

EXPOSE 5003
CMD ["python3", "server.py"]
