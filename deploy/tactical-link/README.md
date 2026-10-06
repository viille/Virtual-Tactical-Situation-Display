# TacticalLink server deployment

This service keeps connected presence and telemetry in memory. It has no database, Redis dependency, or track persistence.

## Configure

Place a `.env` file beside `docker-compose.yml` on the Hetzner host. Do not commit it. Configure the RS256 public key corresponding to the private key used by VTSD Cloud:

```dotenv
TACTICAL_LINK_JWT_PUBLIC_KEY="-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----"
TACTICAL_LINK_JWT_ISSUER=https://www.vtsd.app
TACTICAL_LINK_JWT_AUDIENCE=tactical-link
TACTICAL_LINK_JWT_KEY_ID=production-2026-01
TACTICAL_LINK_INTEREST_RADIUS_NM=200
TACTICAL_LINK_MAX_INTEREST_RADIUS_NM=500
```

Set `TACTICAL_LINK_JWT_PRIVATE_KEY` and the same `TACTICAL_LINK_JWT_KEY_ID` in the VTSD Cloud deployment, with matching issuer and audience settings. Cloud issues five-minute tokens only after resolving the authenticated VATSIM CID against the official VATSIM feed.

## Run behind Caddy

The compose file starts both the internal service and Caddy, with persistent certificate volumes and bounded JSON logs. Set `TACTICAL_LINK_HOST` if the public host differs from `link.vtsd.app`, then start them:

```sh
docker compose -f deploy/tactical-link/docker-compose.yml up -d --build
```

The server listens only on the Docker network at port 8080. `/healthz` is a liveness check and does not expose participants.

## Operational limits

Interest defaults to 200 NM and clients may request a radius up to the separately configured safe maximum (1 to 1000 NM, default 500 NM). Reconnect grace is 12 seconds; each authenticated session receives a random public participant ID, retained for reconnect grace, and replacement connections receive a higher generation. Outbound queues are bounded; telemetry coalesces to the newest unsent value per peer. Clients whose queues fill are disconnected. Automated in-process tests cover 50 and 100 participants at 20 frames per second; they are not a measurement of the target VPS. Measure on the 2 vCPU / 4 GB host before making capacity claims.
