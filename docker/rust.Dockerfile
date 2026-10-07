ARG RUST_IMAGE=rust:1.91.0-slim-bookworm
FROM ${RUST_IMAGE}

RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 pkg-config libssl-dev \
    && rm -rf /var/lib/apt/lists/* \
    && rustup target add wasm32-unknown-unknown \
    && rustup component add rustfmt clippy \
    && cargo install wasm-bindgen-cli --version 0.2.105 --locked \
    && chmod -R a+rX /usr/local/rustup

# Keep installed tools reachable when CARGO_HOME is a mounted cache.
ENV PATH="/usr/local/cargo/bin:${PATH}"
