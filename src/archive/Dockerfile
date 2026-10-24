FROM node:24-bookworm-slim AS frontend
WORKDIR /build
RUN npm install --global pnpm@11.3.0
COPY src/archive/frontend/package.json src/archive/frontend/pnpm-lock.yaml src/archive/frontend/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile
COPY src/archive/frontend/ ./
ENV NEXT_TELEMETRY_DISABLED=1
RUN pnpm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /build
COPY global.json ./
COPY src/archive/backend/Archive.Backend.csproj src/archive/backend/
COPY src/archive/service-defaults/Archive.ServiceDefaults.csproj src/archive/service-defaults/
RUN dotnet restore src/archive/backend/Archive.Backend.csproj
COPY src/archive/backend/ src/archive/backend/
COPY src/archive/service-defaults/ src/archive/service-defaults/
ARG VERSION=0.1.0
ARG REVISION=local
RUN dotnet publish src/archive/backend/Archive.Backend.csproj -c Release -o /app --no-restore /p:Version=$VERSION /p:SourceRevisionId=$REVISION /p:UseAppHost=false
COPY --from=frontend /build/out/ /app/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=backend /app/ ./
ENV ASPNETCORE_HTTP_PORTS=8080
# #161: the transcode job shells out to ffmpeg/ffprobe (H.264 x264 and AAC
# encoders ship in the distro package); the image therefore needs both on the
# PATH so the deterministic MissingTool failure never fires in production.
RUN apt-get update \
	&& apt-get install -y --no-install-recommends ffmpeg \
	&& rm -rf /var/lib/apt/lists/*
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Archive.Backend.dll"]
