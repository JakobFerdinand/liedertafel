FROM node:24-bookworm-slim AS frontend
WORKDIR /build
RUN npm install --global pnpm@11.3.0
COPY src/archive/frontend/package.json src/archive/frontend/pnpm-lock.yaml src/archive/frontend/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/archive/frontend/ ./
ENV NEXT_TELEMETRY_DISABLED=1
RUN pnpm run build

FROM docker.io/library/rust:1.99.1-bookworm AS backend
ARG TARGETARCH
ARG BUILD_JOBS=4
RUN test "$TARGETARCH" = amd64
WORKDIR /native
RUN curl -fsSL https://github.com/openssl/openssl/releases/download/openssl-3.5.9/openssl-3.5.9.tar.gz -o openssl.tar.gz \
    && echo '603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a  openssl.tar.gz' | sha256sum -c - \
    && tar -xzf openssl.tar.gz \
    && cd openssl-3.5.9 \
    && perl Configure --prefix=/opt/archive-openssl --openssldir=/etc/ssl no-shared no-tests \
    && make -j "$BUILD_JOBS" \
    && make install_sw
RUN curl -fsSL https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8138/pdfium-linux-x64.tgz -o pdfium.tgz \
    && echo 'd6b1e28950a0392590d79bbdbb7eb19686872b688f8a36f170acb19e41473113  pdfium.tgz' | sha256sum -c - \
    && mkdir pdfium && tar -xzf pdfium.tgz -C pdfium \
    && echo '655e831208783f38dc187ad424bdfdae39858cfbfcf3a9a2cb9dd0694b5abcad  pdfium/lib/libpdfium.so' | sha256sum -c -

WORKDIR /build
COPY src/archive/backend-rs/Cargo.toml src/archive/backend-rs/Cargo.lock src/archive/backend-rs/rust-toolchain.toml ./
COPY src/archive/backend-rs/crates/ crates/
ARG VERSION=0.1.0
# Keep the precise covered MPL source artifacts (which include their notices).
RUN --mount=type=cache,id=archive-235-cargo-registry,target=/usr/local/cargo/registry \
    --mount=type=cache,id=archive-235-cargo-target,target=/cargo-target \
    ARCHIVE_IMAGE_VERSION="$VERSION" OPENSSL_DIR=/opt/archive-openssl OPENSSL_STATIC=1 \
    CARGO_TARGET_DIR=/cargo-target \
    cargo build --jobs "$BUILD_JOBS" --locked --release -p archive-server --bin archive-server \
    && install -D /cargo-target/release/archive-server /artifacts/archive-server \
    && mkdir -p /notices/mpl-source \
    && for crate in base64urlsafedata webauthn-attestation-ca webauthn-rs-core webauthn-rs-proto; do \
         cp "$CARGO_HOME"/registry/cache/*/"$crate"-0.5.5.crate /notices/mpl-source/ || exit 1; \
       done \
    && cp /native/openssl-3.5.9/LICENSE.txt /notices/OpenSSL-LICENSE.txt \
    && mkdir /notices/pdfium \
    && cp -r /native/pdfium/LICENSE /native/pdfium/licenses /native/pdfium/VERSION /native/pdfium/args.gn /notices/pdfium/

FROM docker.io/library/debian:bookworm-slim AS runtime
ARG VERSION=0.1.0
ARG REVISION=local
LABEL org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION"
WORKDIR /app
# Keep Debian's package copyright files, including ffmpeg/libx264 source information.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates ffmpeg fonts-dejavu-core libstdc++6 \
    && rm -rf /var/lib/apt/lists/* \
    && install -d -m 0700 -o 10001 -g 10001 /app/scratch
COPY --from=frontend /build/out/ /app/wwwroot/
COPY --from=backend /artifacts/archive-server /app/archive-server
COPY --from=backend /native/pdfium/lib/libpdfium.so /app/native/libpdfium.so
COPY --from=backend /notices/ /usr/share/doc/archive-native/
COPY src/archive/backend-rs/docs/image-native-notices.md /usr/share/doc/archive-native/README.md
COPY src/archive/backend-rs/tests/oracles/dotnet-runtime-LICENSE.txt /usr/share/doc/archive-native/
ENV PORT=8080 \
    ARCHIVE_STATIC_ROOT=/app/wwwroot \
    Archive__Pdfium__LibraryPath=/app/native/libpdfium.so \
    Archive__Pdfium__SpoolDirectory=/app/scratch \
    TMPDIR=/app/scratch
USER 10001:10001
EXPOSE 8080
ENTRYPOINT ["/app/archive-server"]
