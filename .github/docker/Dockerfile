# Fork image: the linuxserver image of the same upstream version (or its develop image before
# that version is released) with the app binaries replaced.
# Build context: the _artifacts directory of build.sh --packages.
ARG BASE_TAG

FROM scratch AS bin-amd64
COPY linux-musl-x64/net10.0/Radarr /

FROM scratch AS bin-arm64
COPY linux-musl-arm64/net10.0/Radarr /

FROM bin-${TARGETARCH} AS bin

FROM lscr.io/linuxserver/radarr:${BASE_TAG}
RUN rm -rf /app/radarr/bin
COPY --from=bin --chown=1001:1001 --chmod=755 / /app/radarr/bin/
ARG FORK_VERSION
RUN sed -i -e "s|^PackageVersion=.*|PackageVersion=${FORK_VERSION}|" \
      -e "s|^PackageAuthor=.*|PackageAuthor=[Ruakij](https://github.com/Ruakij/Radarr)|" /app/radarr/package_info
