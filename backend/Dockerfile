# Stage 1: build
FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

COPY backend.sln .
COPY src/backend.Api/backend.csproj src/backend.Api/
COPY src/backend.Application/backend.Application.csproj src/backend.Application/
COPY src/backend.Domain/backend.Domain.csproj src/backend.Domain/
COPY src/backend.Infrastructure/backend.Infrastructure.csproj src/backend.Infrastructure/
COPY tests/unit/Domain.Tests/Domain.Tests.csproj tests/unit/Domain.Tests/
COPY tests/unit/Application.Tests/Application.Tests.csproj tests/unit/Application.Tests/

RUN dotnet restore

COPY src/ src/
COPY tests/ tests/
RUN dotnet publish src/backend.Api/backend.csproj -c Release -o /app/publish --no-restore

# Stage 2: run
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app

RUN adduser -D -g "" appuser
COPY --from=build /app/publish .
USER appuser

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "backend.dll"]
