# Build context is the repo root (this project's shared dependencies live in ../shared).
# Build:  docker build -f web/Dockerfile -t kawapaint-web .   (run from the repo root)
# Run:    docker run --rm -p 8080:80 kawapaint-web

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Emscripten (used to compile the WASM native runtime) shells out to python3, which the base
# SDK image doesn't include.
RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && ln -sf /usr/bin/python3 /usr/bin/python \
    && rm -rf /var/lib/apt/lists/*

# Restore from just the project files first, so this (slow) layer only re-runs when a .csproj
# actually changes, not on every source edit.
COPY shared/KawaPaint.Engine/KawaPaint.Engine.csproj shared/KawaPaint.Engine/
COPY shared/KawaPaint.App/KawaPaint.App.csproj shared/KawaPaint.App/
COPY web/KawaPaint.Web.csproj web/
RUN dotnet workload install wasm-tools
RUN dotnet restore web/KawaPaint.Web.csproj

COPY shared/KawaPaint.Engine/ shared/KawaPaint.Engine/
COPY shared/KawaPaint.App/ shared/KawaPaint.App/
COPY web/ web/
RUN dotnet publish web/KawaPaint.Web.csproj -c Release -o /app/publish

# Runtime: the published output is static files (HTML/JS/WASM) - no .NET runtime needed to serve it.
FROM nginx:alpine AS final
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
COPY web/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
