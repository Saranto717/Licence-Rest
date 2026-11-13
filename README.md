# RestaurantOrders Cloud License Service

This repository contains the cloud API, web-based License Manager, PostgreSQL migrations, HTTPS reverse proxy configuration, and GitHub Actions workflow that publishes the API container image to GHCR.

Deployment instructions: [`deploy/cloud/README-EL.md`](deploy/cloud/README-EL.md).

**Secrets are never stored in this repository.** Keep the VPS `.env`, GHCR read token, and RSA license-signing private key outside GitHub. The Windows POS source is maintained separately.
