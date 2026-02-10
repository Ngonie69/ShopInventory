# ShopInventory Production Deployment Guide

## Overview

This guide covers deploying ShopInventory (API + Blazor Web) to production using Docker containers.

## Architecture

```
???????????????????     ???????????????????     ???????????????????
?   Nginx/LB      ???????  Blazor Web     ???????   API Server    ?
?   (Port 80/443) ?     ?  (Port 5107)    ?     ?   (Port 5106)   ?
???????????????????     ???????????????????     ???????????????????
                                ?                        ?
                                ?                        ?
                                ?                        ?
                        ???????????????????     ???????????????????
                        ?   PostgreSQL    ?     ?   SAP B1        ?
                        ?   (Web Cache)   ?     ?   Service Layer ?
                        ???????????????????     ???????????????????
```

## Prerequisites

- Docker 24+ and Docker Compose v2
- PostgreSQL 16+ (or use the included container)
- SSL certificates for HTTPS
- Access to SAP Business One Service Layer
- .NET 10 SDK (for local development only)

## Quick Start

### 1. Clone and Configure

```bash
# Clone the repository
git clone https://github.com/Ngonie69/ShopInventory.git
cd ShopInventory

# Copy and configure environment variables
cp .env.example .env
nano .env  # Edit with your production values
```

### 2. Configure Environment Variables

Edit `.env` with your production values:

| Variable | Description | Required |
|----------|-------------|----------|
| `POSTGRES_PASSWORD` | Database password | ? |
| `JWT_SECRET_KEY` | JWT signing key (64+ chars) | ? |
| `API_KEY_MAIN` | API authentication key | ? |
| `SAP_SERVICE_URL` | SAP B1 Service Layer URL | ? |
| `SAP_COMPANY_DB` | SAP company database name | ? |
| `SAP_USERNAME` | SAP username | ? |
| `SAP_PASSWORD` | SAP password | ? |
| `WEB_APP_URL` | Public URL of web app | ? |
| `SMTP_*` | Email configuration | Optional |
| `OPENAI_API_KEY` | For AI features | Optional |

### 3. Build and Deploy

```bash
# Build images
docker-compose build

# Start services
docker-compose up -d

# View logs
docker-compose logs -f
```

### 4. Verify Deployment

```bash
# Check service health
docker-compose ps

# Test API
curl http://localhost:5106/swagger/index.html

# Test Web app
curl http://localhost:5107/
```

## Production Deployment Options

### Option A: Docker Compose (Single Server)

Best for small to medium deployments.

```bash
# Production deployment
docker-compose -f docker-compose.yml up -d

# With Nginx reverse proxy
docker-compose -f docker-compose.yml --profile with-nginx up -d
```

### Option B: Kubernetes

For scalable cloud deployments, convert to Kubernetes manifests:

```bash
# Generate Kubernetes manifests (requires kompose)
kompose convert -f docker-compose.yml
```

### Option C: Azure Container Apps / AWS ECS

Both projects are container-ready. Deploy using your cloud provider's container service.

## SSL/HTTPS Configuration

### Using Nginx (Recommended)

1. Create SSL directory:
```bash
mkdir -p nginx/ssl
```

2. Copy your certificates:
```bash
cp your-cert.crt nginx/ssl/server.crt
cp your-key.key nginx/ssl/server.key
```

3. Create `nginx/nginx.conf`:
```nginx
events {
    worker_connections 1024;
}

http {
    upstream api {
        server shopinventory-api:5106;
    }

    upstream web {
        server shopinventory-web:5107;
    }

    server {
        listen 80;
        server_name your-domain.com;
        return 301 https://$server_name$request_uri;
    }

    server {
        listen 443 ssl http2;
        server_name your-domain.com;

        ssl_certificate /etc/nginx/ssl/server.crt;
        ssl_certificate_key /etc/nginx/ssl/server.key;
        ssl_protocols TLSv1.2 TLSv1.3;

        # Web app
        location / {
            proxy_pass http://web;
            proxy_http_version 1.1;
            proxy_set_header Upgrade $http_upgrade;
            proxy_set_header Connection "upgrade";
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
        }

        # API endpoints
        location /api {
            proxy_pass http://api;
            proxy_set_header Host $host;
            proxy_set_header X-Real-IP $remote_addr;
            proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
            proxy_set_header X-Forwarded-Proto $scheme;
        }

        # Swagger UI
        location /swagger {
            proxy_pass http://api;
            proxy_set_header Host $host;
        }
    }
}
```

4. Deploy with Nginx profile:
```bash
docker-compose --profile with-nginx up -d
```

## Database Management

### Initial Setup

The PostgreSQL container automatically creates databases on first run.

### Migrations

Run migrations after deployment:

```bash
# API database migrations
docker exec shopinventory-api dotnet ef database update

# Web cache database migrations  
docker exec shopinventory-web dotnet ef database update
```

### Backups

```bash
# Backup database
docker exec shopinventory-db pg_dump -U shopinventory shopinventory > backup.sql

# Restore database
docker exec -i shopinventory-db psql -U shopinventory shopinventory < backup.sql
```

## Monitoring & Logs

### View Logs

```bash
# All services
docker-compose logs -f

# Specific service
docker-compose logs -f shopinventory-api
docker-compose logs -f shopinventory-web
```

### Health Checks

Both services include health check endpoints:
- API: `http://localhost:5106/swagger/index.html`
- Web: `http://localhost:5107/`

### Log Locations (in containers)

- API: `/app/logs/`
- Web: `/app/logs/`

## Security Checklist

Before going live, ensure:

- [ ] Strong passwords in `.env`
- [ ] JWT secret is 64+ characters
- [ ] API key is unique and secure
- [ ] SSL/HTTPS is configured
- [ ] Database is not exposed externally (remove port mapping in production)
- [ ] Firewall rules are configured
- [ ] `Security.AllowedOrigins` contains only your domain
- [ ] Rate limiting is enabled
- [ ] Regular backups are scheduled

## Troubleshooting

### Container won't start

```bash
# Check logs
docker-compose logs shopinventory-api

# Common issues:
# - Database not ready: wait for postgres health check
# - Missing environment variables: check .env file
```

### API returns 500 errors

```bash
# Check API logs
docker-compose logs shopinventory-api | grep -i error

# Common issues:
# - SAP connection failed: verify SAP_* variables
# - Database connection: verify POSTGRES_* variables
```

### Blazor SignalR disconnects

Ensure WebSocket support in your reverse proxy:
```nginx
proxy_http_version 1.1;
proxy_set_header Upgrade $http_upgrade;
proxy_set_header Connection "upgrade";
```

## Scaling

### Horizontal Scaling

For high availability, run multiple instances behind a load balancer:

```yaml
# docker-compose.override.yml
services:
  shopinventory-web:
    deploy:
      replicas: 3
```

**Note:** For multiple API instances, replace `InMemoryInventoryLockService` with Redis-based locking.

## Support

- Documentation: See `/docs` folder
- Issues: GitHub Issues
- Email: support@shopinventory.co.zw
