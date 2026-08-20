# Build Stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files for caching package restore
COPY ["GSTAutoPilot.API/GSTAutoPilot.API.csproj", "GSTAutoPilot.API/"]
COPY ["GSTAutoPilot.Application/GSTAutoPilot.Application.csproj", "GSTAutoPilot.Application/"]
COPY ["GSTAutoPilot.Domain/GSTAutoPilot.Domain.csproj", "GSTAutoPilot.Domain/"]
COPY ["GSTAutoPilot.Infrastructure/GSTAutoPilot.Infrastructure.csproj", "GSTAutoPilot.Infrastructure/"]

# Restore dependencies
RUN dotnet restore "GSTAutoPilot.API/GSTAutoPilot.API.csproj"

# Copy full source and build
COPY . .
WORKDIR "/src/GSTAutoPilot.API"
RUN dotnet publish "GSTAutoPilot.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime Stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Configure ASP.NET Core environment
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "GSTAutoPilot.API.dll"]
