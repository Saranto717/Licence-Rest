# RestaurantOrders Cloud License Service

This repository contains the cloud API and web-based License Manager.

- `Dockerfile` builds the API image.
- `docker-compose.yml` pulls the versioned image from GitHub Container Registry (GHCR) and runs it with PostgreSQL.
- GitHub Actions builds and publishes images for `main` and version tags. A version tag such as `v17.3.113` updates `latest` and can trigger automatic VPS deployment.

Greek deployment instructions: [`README-DEPLOY-EL.md`](README-DEPLOY-EL.md).

**Secrets are never stored in this repository.** Keep the VPS `.env`, GHCR read token, SSH private key, and RSA license-signing private key outside Git. The Windows POS source is maintained separately.
