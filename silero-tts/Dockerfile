FROM debian:bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl ca-certificates python3 python3-pip \
    && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir --break-system-packages \
    flask numpy \
    torch --index-url https://download.pytorch.org/whl/cpu

RUN mkdir -p /models \
    && curl -sL -o /models/v4_ru.pt https://models.silero.ai/models/tts/ru/v4_ru.pt

WORKDIR /app
COPY server.py .

EXPOSE 5002
CMD ["python3", "server.py"]
