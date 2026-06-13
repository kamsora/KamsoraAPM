# KamsoraAPM Sample Apps

End-to-end demos that consume the KamsoraAPM Agent. Use these to verify your
local stack works.

## SampleWebApi

A minimal .NET 8 Web API wired with `KamsoraAPM.Agent` via the
`AddKamsoraApm` extension. Every HTTP request produces one server span,
which the Agent converts and exports to the Collector.

### Run locally

1. **Bring up dependencies + the Collector + Dashboard.Api**

   ```bash
   docker compose -f deploy/docker/docker-compose.yml --profile apps up -d
   ```

2. **Seed a tenant** (the Dashboard.Api seeder logs the cleartext API key
   exactly once at startup). Configure `KamsoraApm:Auth:SeedTenant` in
   `src/KamsoraAPM.Dashboard.Api/appsettings.json` before the first run.

3. **Update `sample-apps/SampleWebApi/appsettings.json`** with the seeded
   tenant UUID and API key.

4. **Run the sample**:

   ```bash
   dotnet run --project sample-apps/SampleWebApi
   ```

5. **Hit some endpoints**:

   ```bash
   curl http://localhost:5085/weather
   curl http://localhost:5085/items/3
   curl http://localhost:5085/items/17   # synthetic 500
   curl http://localhost:5085/boom       # throws
   ```

6. **Query the dashboard API** (after authenticating to get a JWT):

   ```bash
   curl -s -X POST http://localhost:5090/api/v1/auth/login \
        -H 'Content-Type: application/json' \
        -d '{"email":"you@example.com","password":"..."}' \
        | jq -r .accessToken
   curl http://localhost:5090/api/v1/traces \
        -H "Authorization: Bearer $TOKEN"
   ```

Until M1.11 ships, the React SPA is not yet wired up — the REST endpoint
above is the demo surface.
