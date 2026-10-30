FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends clang zlib1g-dev python3 git util-linux libgtk-3-0t64 libgtk-4-1 libfontconfig1 libfreetype6 libssl3t64 xvfb xauth libxtst6 libsecret-tools gnome-keyring dbus-x11 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_PROCESSOR_COUNT=2 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
