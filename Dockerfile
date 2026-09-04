# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src
RUN apt-get -o Acquire::Retries=5 update \
    && apt-get -o Acquire::Retries=5 install -y --no-install-recommends clang llvm zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*
COPY . .
ARG TARGETARCH
RUN set -eu; \
    arch="$TARGETARCH"; \
    if [ "$arch" = "amd64" ]; then arch="x64"; fi; \
    rid="linux-$arch"; \
    dotnet restore src/SlimVector.Api/SlimVector.Api.csproj -r "$rid"; \
    dotnet publish src/SlimVector.Api/SlimVector.Api.csproj \
        -c Release \
        -r "$rid" \
        --self-contained true \
        --no-restore \
        -o /app; \
    mkdir -p /runtime/data

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    Storage__Path=/data
EXPOSE 8080 3262 3263 3264
VOLUME ["/data"]
COPY --from=build /app/SlimVector.Api /app/SlimVector.Api
COPY --from=build --chown=$APP_UID:$APP_UID /runtime/data /data
USER $APP_UID
ENTRYPOINT ["/app/SlimVector.Api"]
