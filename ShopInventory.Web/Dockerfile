# ShopInventory Web Dockerfile
# Multi-stage build for optimized production image

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy project file and restore dependencies
COPY ["ShopInventory.Web/ShopInventory.Web.csproj", "ShopInventory.Web/"]
RUN dotnet restore "ShopInventory.Web/ShopInventory.Web.csproj"

# Copy source code and build
COPY ["ShopInventory.Web/", "ShopInventory.Web/"]
WORKDIR "/src/ShopInventory.Web"
RUN dotnet build "ShopInventory.Web.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "ShopInventory.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

# Create non-root user for security
RUN groupadd -r appgroup && useradd -r -g appgroup appuser

# Create directories for logs
RUN mkdir -p /app/logs && \
    chown -R appuser:appgroup /app

# Copy published app
COPY --from=publish /app/publish .

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5107

# Expose port
EXPOSE 5107

# Switch to non-root user
USER appuser

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:5107/ || exit 1

# Entry point
ENTRYPOINT ["dotnet", "ShopInventory.Web.dll"]
