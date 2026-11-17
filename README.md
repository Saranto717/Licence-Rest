# RestaurantOrders Cloud License Service

This repository contains the cloud API and web-based License Manager for RestaurantOrders.

- `Dockerfile` builds the API image.
- `docker-compose.yml` pulls the image from GitHub Container Registry (GHCR) and runs it with PostgreSQL.
- GitHub Actions builds every `main` commit. Publishing a stable GitHub Release such as `v0.2.0` publishes both `:v0.2.0` and `:latest`.
- Dockhand stays on `main`; its scheduled sync re-pulls `latest` and redeploys the API after a release.

The License Manager provides customer profiles, license editions, license renewal and revocation, and installation assignments. See [`README-DEPLOY-EL.md`](README-DEPLOY-EL.md) for setup and release steps.

Keep database credentials, manager credentials, registry credentials, and license-signing keys out of Git. Back up the PostgreSQL data and the signing key before changing server storage.
