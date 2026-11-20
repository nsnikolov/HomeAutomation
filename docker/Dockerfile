FROM node:24-bookworm-slim AS frontend
WORKDIR /web
COPY src/frontend/home-automation-web/package*.json ./
RUN npm ci --no-audit --no-fund
COPY src/frontend/home-automation-web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY src/backend/HomeAutomation.Domain/*.csproj src/backend/HomeAutomation.Domain/
COPY src/backend/HomeAutomation.Application/*.csproj src/backend/HomeAutomation.Application/
COPY src/backend/HomeAutomation.Infrastructure/*.csproj src/backend/HomeAutomation.Infrastructure/
COPY src/backend/HomeAutomation.Api/*.csproj src/backend/HomeAutomation.Api/
RUN dotnet restore src/backend/HomeAutomation.Api/HomeAutomation.Api.csproj
COPY src/backend/ src/backend/
COPY database/ database/
RUN dotnet publish src/backend/HomeAutomation.Api/HomeAutomation.Api.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out/ ./
COPY --from=frontend /web/dist/ ./wwwroot/
RUN mkdir -p /app/keys && chown app:app /app/keys
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "HomeAutomation.Api.dll"]
