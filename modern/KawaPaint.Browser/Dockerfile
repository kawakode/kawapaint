# Build context is the `modern/` folder (this project's siblings — Engine, App — live there too).
# Build:  docker build -f KawaPaint.Browser/Dockerfile -t kawapaint-web .   (run from modern/)
# Run:    docker run --rm -p 8080:80 kawapaint-web

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore from just the project files first, so this (slow) layer only re-runs when a .csproj
# actually changes, not on every source edit.
COPY KawaPaint.Engine/KawaPaint.Engine.csproj KawaPaint.Engine/
COPY KawaPaint.App/KawaPaint.App.csproj KawaPaint.App/
COPY KawaPaint.Browser/KawaPaint.Browser.csproj KawaPaint.Browser/
RUN dotnet workload install wasm-tools
RUN dotnet restore KawaPaint.Browser/KawaPaint.Browser.csproj

COPY KawaPaint.Engine/ KawaPaint.Engine/
COPY KawaPaint.App/ KawaPaint.App/
COPY KawaPaint.Browser/ KawaPaint.Browser/
RUN dotnet publish KawaPaint.Browser/KawaPaint.Browser.csproj -c Release -o /app/publish

# Runtime: the published output is static files (HTML/JS/WASM) — no .NET runtime needed to serve it.
FROM nginx:alpine AS final
COPY --from=build /app/publish/wwwroot /usr/share/nginx/html
COPY KawaPaint.Browser/nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
