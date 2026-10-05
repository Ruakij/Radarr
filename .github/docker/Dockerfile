# Fork image: the linuxserver image of the same upstream version with the app binaries replaced.
# Build context: the _artifacts directory of build.sh --packages.
ARG BASE_VERSION

FROM scratch AS bin-amd64
COPY linux-musl-x64/net8.0/Radarr /

FROM scratch AS bin-arm64
COPY linux-musl-arm64/net8.0/Radarr /

FROM bin-${TARGETARCH} AS bin

FROM lscr.io/linuxserver/radarr:${BASE_VERSION}
RUN rm -rf /app/radarr/bin
COPY --from=bin --chown=1001:1001 / /app/radarr/bin/
ARG FORK_VERSION
RUN sed -i -e "s|^PackageVersion=.*|PackageVersion=${FORK_VERSION}|" \
      -e "s|^PackageAuthor=.*|PackageAuthor=[Ruakij](https://github.com/Ruakij/Radarr)|" /app/radarr/package_info
