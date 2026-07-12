FROM postgres:17-alpine3.21 AS builder

ARG PGVECTOR_VERSION=0.8.2

RUN apk add --no-cache build-base curl postgresql17-dev
RUN curl -fsSL "https://github.com/pgvector/pgvector/archive/refs/tags/v${PGVECTOR_VERSION}.tar.gz" \
        | tar -xz -C /tmp \
    && cd "/tmp/pgvector-${PGVECTOR_VERSION}" \
    && make PG_CONFIG=/usr/bin/pg_config OPTFLAGS="" \
    && make PG_CONFIG=/usr/bin/pg_config DESTDIR=/tmp/install install

# Keep the same Alpine runtime and postgres UID (70) as the existing volume.
# The builder package installs into Alpine's /usr paths; copy only the runtime
# extension into the /usr/local tree used by the official postgres image.
FROM postgres:17-alpine

COPY --from=builder /tmp/install/usr/lib/postgresql17/vector.so /usr/local/lib/postgresql/vector.so
COPY --from=builder /tmp/install/usr/share/postgresql17/extension/ /usr/local/share/postgresql/extension/
