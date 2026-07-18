# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/SlimVector.Api/SlimVector.Api.csproj -r linux-x64
RUN dotnet publish src/SlimVector.Api/SlimVector.Api.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    --no-restore \
    -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    Storage__Path=/data
EXPOSE 8080 3262 3263 3264
VOLUME ["/data"]
COPY --from=build /app/SlimVector.Api /app/SlimVector.Api
USER $APP_UID
ENTRYPOINT ["/app/SlimVector.Api"]
