# Deploying AegisCode

AegisCode is three pieces: **Vue frontend** (static), **.NET 10 API** (Docker),
**PostgreSQL 16 + pgvector** (managed). Deploy the backend first, then point the
frontend at it.

## 1. Backend + database (Railway or Render)

### Render (blueprint)

1. Render dashboard -> New -> **Blueprint** -> connect `Prateek2007-cmd/aegiscode`.
2. Render reads `render.yaml` and creates the `aegiscode-api` web service (from
   `Dockerfile.deploy`) plus the `aegiscode-db` Postgres database.
3. When the database is up, open it -> **Connect -> PSQL** and run:
   ```sql
   CREATE EXTENSION IF NOT EXISTS vector;
   ```
4. Note the API's public URL, e.g. `https://aegiscode-api.onrender.com`.
5. Set `MEISTER_BOOTSTRAP_ADMIN_PASSWORD` from the generated value (Render ->
   service -> Environment) — you'll need it for the first sign-in.

### Railway (manual)

1. New Project -> **Deploy from GitHub repo** -> select `aegiscode`.
2. Service Settings -> **Dockerfile Path** = `Dockerfile.deploy`.
3. Add a **PostgreSQL** plugin; in its PSQL console run:
   ```sql
   CREATE EXTENSION IF NOT EXISTS vector;
   ```
4. On the API service -> Variables, set:
   - `DB_CONNECTION_STRING` = `${{Postgres.DATABASE_URL}}`
     (Npgsql accepts the `postgresql://` URL form)
   - `MEISTER_JWT_SECRET` = any 32+ char secret (generate one)
   - `MEISTER_BOOTSTRAP_ADMIN_USER` = `admin`
   - `MEISTER_BOOTSTRAP_ADMIN_PASSWORD` = your first admin password
   - `CORS_ORIGINS` = your Vercel frontend URL (step 2)
   - `AEGIS_SECURITY_MAX_CONCURRENT_SCANS` = `1`
5. Deploy; note the public domain Railway assigns.

The API runs EF Core migrations automatically on startup and seeds the admin
user on first boot. Health check: `GET /healthz`.

## 2. Frontend (Vercel)

1. Vercel -> Add New -> Project -> import `Prateek2007-cmd/aegiscode`.
2. **Root Directory** = `frontend`. Framework preset: Vite.
3. Build command: `npm run build`. Output: `dist`.
4. Environment variable:
   - `VITE_API_BASE_URL` = `https://<your-api-domain>` (no trailing slash)
5. Deploy. Open the URL, sign in with the bootstrap admin.

## 3. Wire-up check

- `CORS_ORIGINS` on the API must exactly match the Vercel origin
  (`https://<project>.vercel.app`), otherwise browsers block API calls.
- After changing `CORS_ORIGINS`, redeploy/restart the API service.

## Local Docker (optional)

```bash
docker build -f Dockerfile.deploy -t aegiscode .
docker run --env-file .env.deploy -p 8080:8080 aegiscode
```

`.env.deploy` needs the same variables as above (never commit it).
