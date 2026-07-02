FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS build
ARG TARGETPLATFORM
COPY publish /build
RUN cp -r /build/${TARGETPLATFORM} /publish

# Debian/glibc, not Alpine/musl: this process P/Invokes into native libpcap
# (SharpPcap, A&D traffic capture — ContainerProvider:EnableTrafficCapture),
# ICU, libgdiplus and krb5, and a native SIGSEGV inside musl's vfscanf took
# the platform down for ~55min with no stack trace to go on. glibc's libc is
# far more battle-tested for this kind of native interop. ICU and tzdata are
# already bundled in this base image, unlike the Alpine variant.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    LC_ALL=en_US.UTF-8

WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends \
    wget libpcap0.8t64 ca-certificates libgdiplus libkrb5-3 git openssh-client && \
    update-ca-certificates && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build --chown=$APP_UID:$APP_UID /publish .

EXPOSE 8080

HEALTHCHECK --interval=5m --timeout=3s --start-period=10s --retries=1 \
    CMD wget --no-verbose --tries=1 --spider http://localhost:3000/healthz || exit 1

ENTRYPOINT ["dotnet", "GZCTF.dll"]
