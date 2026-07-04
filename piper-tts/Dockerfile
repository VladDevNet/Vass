FROM debian:bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    curl ca-certificates python3 python3-pip \
    && rm -rf /var/lib/apt/lists/*

RUN pip install --no-cache-dir --break-system-packages flask

RUN curl -sL -o /tmp/piper.tar.gz https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_linux_x86_64.tar.gz \
    && tar -xzf /tmp/piper.tar.gz -C /opt \
    && rm /tmp/piper.tar.gz \
    && chmod +x /opt/piper/piper

RUN mkdir -p /models \
    && curl -sL -o /models/ru_RU-irina-medium.onnx https://huggingface.co/rhasspy/piper-voices/resolve/main/ru/ru_RU/irina/medium/ru_RU-irina-medium.onnx \
    && curl -sL -o /models/ru_RU-irina-medium.onnx.json https://huggingface.co/rhasspy/piper-voices/resolve/main/ru/ru_RU/irina/medium/ru_RU-irina-medium.onnx.json

WORKDIR /app
COPY server.py .

EXPOSE 5001
CMD ["python3", "server.py"]
