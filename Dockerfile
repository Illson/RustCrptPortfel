FROM mcr.microsoft.com/dotnet/sdk:11.0.100 AS backend
WORKDIR /source
COPY . .
RUN dotnet publish src/App.Api -c Release -o /out/api
RUN dotnet publish src/App.Worker -c Release -o /out/worker

FROM node:24.22.3-bookworm-slim AS frontend
WORKDIR /source
RUN npm install --global pnpm@11.25.0
COPY frontend/package.json frontend/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY frontend/ .
RUN pnpm build

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0 AS api
WORKDIR /app
COPY --from=backend /out/api .
COPY --from=frontend /source/dist /app/frontend
ENV Frontend__Directory=/app/frontend ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "App.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0 AS worker
WORKDIR /app
COPY --from=backend /out/worker .
ENTRYPOINT ["dotnet", "App.Worker.dll"]
