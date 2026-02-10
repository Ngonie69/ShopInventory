# ShopInventory API Dockerfile
# Multi-stage build for optimized production image

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["ShopInventory/ShopInventory.csproj", "ShopInventory/"]
RUN dotnet restore "ShopInventory/ShopInventory.csproj"

# Copy source code and build
COPY ["ShopInventory/", "ShopInventory/"]
WORKDIR "/src/ShopInventory"
RUN dotnet build "ShopInventory.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "ShopInventory.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

# Create non-root user for security
RUN groupadd -r appgroup && useradd -r -g appgroup appuser

# Create directories for uploads and logs
RUN mkdir -p /app/uploads /app/logs && \
    chown -R appuser:appgroup /app

# Copy published app
COPY --from=publish /app/publish .

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5106

# Expose port
EXPOSE 5106

# Switch to non-root user
USER appuser

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:5106/swagger/index.html || exit 1

# Entry point
ENTRYPOINT ["dotnet", "ShopInventory.dll"]
