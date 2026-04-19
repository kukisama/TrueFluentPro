# ═══ TrueFluentPro Gateway — Multi-stage Dockerfile ═══
#
# Build:  docker build -t truefluentpro-gateway .
# Run:    docker run -p 8080:8080 -v ./data:/app/data truefluentpro-gateway
#
# The gateway stores its SQLite DB under /app/data.
# Mount a volume to persist state across container restarts.

# ─── Stage 1: Build ───
FROM rust:1.82-slim-bookworm AS builder

RUN apt-get update && apt-get install -y --no-install-recommends \
    pkg-config libssl-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /build

# Copy workspace manifests first (cache layer)
COPY Cargo.toml Cargo.lock ./
COPY crates/gateway/Cargo.toml       crates/gateway/Cargo.toml
COPY crates/domain/Cargo.toml        crates/domain/Cargo.toml
COPY crates/storage/Cargo.toml       crates/storage/Cargo.toml
COPY crates/providers/Cargo.toml     crates/providers/Cargo.toml
COPY crates/credential-broker/Cargo.toml crates/credential-broker/Cargo.toml
COPY crates/cache/Cargo.toml         crates/cache/Cargo.toml
COPY crates/billing/Cargo.toml       crates/billing/Cargo.toml
COPY crates/observability/Cargo.toml crates/observability/Cargo.toml

# Create dummy source files to build dependency layer
RUN mkdir -p crates/gateway/src && echo "fn main(){}" > crates/gateway/src/main.rs \
    && for crate in domain storage providers credential-broker cache billing observability; do \
       mkdir -p crates/$crate/src && echo "" > crates/$crate/src/lib.rs; \
    done

RUN cargo build --release 2>/dev/null || true

# Copy real source
COPY crates crates
COPY config config

# Force rebuild of real source (touch timestamps)
RUN find crates -name "*.rs" -exec touch {} +

RUN cargo build --release --bin gateway

# ─── Stage 2: Runtime ───
FROM debian:bookworm-slim AS runtime

RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN useradd -m -u 1001 gateway

WORKDIR /app

COPY --from=builder /build/target/release/gateway /app/gateway
COPY config/default.toml /app/config/default.toml

RUN mkdir -p /app/data && chown -R gateway:gateway /app

USER gateway

ENV GATEWAY_SERVER__BIND="0.0.0.0:8080"
ENV GATEWAY_OBSERVABILITY__LOG_FORMAT="json"
ENV GATEWAY_STORAGE__DATA_DIR="/app/data"

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD ["/app/gateway", "--health-check"]

ENTRYPOINT ["/app/gateway"]
