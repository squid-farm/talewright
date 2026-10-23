FROM node:22-bookworm-slim@sha256:ea11b74b986d520137cdef9a5346e447a00f275ecc4abc0055b7151226231e09 AS node
FROM mcr.microsoft.com/playwright/dotnet:v1.63.0-noble@sha256:4bb37fb71e75c18b6f8b8e4230139c7654de3dd39e55b66e84392c93933fed4e

LABEL org.opencontainers.image.source="https://github.com/squid-farm/talewright"
LABEL org.opencontainers.image.description="Talewright browser and native Linux test dependencies"

USER root
RUN apt-get update && apt-get install -y --no-install-recommends \
      python3 python3-pyatspi python3-gi gir1.2-gtk-3.0 \
      libwebkit2gtk-4.1-0 libnotify4 libgtk-3-0t64 \
      xvfb xauth dbus-x11 at-spi2-core libatk-adaptor xfwm4 wmctrl unzip \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /workspace /opt/talewright-ui \
    && test "$(id -u pwuser)" = 1001 \
    && chown pwuser:pwuser /workspace

COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s ../lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
    && ln -s ../lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx
COPY image.json verify.py /opt/talewright-ui/

ENV HOME=/home/pwuser \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    NX_DAEMON=false
USER 1001:1001
WORKDIR /workspace
