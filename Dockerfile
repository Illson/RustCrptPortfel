FROM mcr.microsoft.com/dotnet/sdk:11.0.100 AS backend
WORKDIR /source
COPY . .
RUN dotnet restore dotnetCrptPortfel.slnx --locked-mode
RUN dotnet publish src/App.Api -c Release --no-restore -o /out/api

FROM node:24.22.3-bookworm-slim AS frontend-build
WORKDIR /source
RUN npm install --global pnpm@11.25.0
COPY frontend/package.json frontend/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY frontend/ .
RUN pnpm build

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0 AS api
WORKDIR /app
COPY --from=backend /out/api .
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "App.Api.dll"]

FROM nginx:1.30.2-alpine AS frontend
COPY frontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=frontend-build /source/dist /usr/share/nginx/html
