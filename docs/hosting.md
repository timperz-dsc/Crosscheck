# Hosting the session server on Railway

This is for running the Crosscheck session server on the internet, so two pilots in different
places can fly together. You only do this once. You do not need to write any code.

You need: a Railway account (Hobby plan) and a GitHub account that can see this repository.

What you will end up with: a server at an address like `wss://crosscheck-production.up.railway.app/ws`.
Each pilot types that address into Crosscheck once.

## 1. Create the project

1. Go to <https://railway.com/dashboard> and log in.
2. Click **New Project**.
3. Click **Deploy from GitHub repo**.
4. If Railway asks to connect GitHub, click **Configure GitHub App**, allow access to the
   Crosscheck repository, and come back.
5. Pick the **crosscheck** repository. Railway starts the first deployment right away.
   Wait for it. It is fine if it fails once; the next steps finish the setup.

Leave **Root Directory** empty (the whole repository). The server needs the `protocol/`
folder next to `server/`, so it cannot be built from `server/` alone. The file `railway.json`
in the repository already tells Railway how to build and start the server (it uses
`server/Dockerfile`, Node 22), where to check that it is alive (`/health`) and to restart it
if it crashes. You do not need to type any build or start command.

## 2. Choose the region

1. Click the service (the box with the repository name) in the project.
2. Open the **Settings** tab.
3. Scroll to **Deploy** → **Region** and choose **EU West (Amsterdam, Netherlands)**.
4. Click **Deploy** (or the change banner at the top) to apply it.

## 3. Add a volume for the session logs

The logs are the evidence when something desyncs. Without a volume they are deleted at every
deployment.

1. In the project, press `Ctrl+K` or right-click the canvas and choose **Volume**
   (or click **+ Create** → **Volume**).
2. Connect it to the Crosscheck service.
3. Set the **mount path** to `/data`.
4. Confirm. Railway redeploys the service.

## 4. Set the log folder variable

1. Click the service → **Variables** tab.
2. Click **New Variable**.
3. Name: `LOG_DIR`   Value: `/data/logs`
4. Click **Add**, then **Deploy** when Railway shows the pending change.

You do not need to set `PORT`. Railway sets it itself.

## 5. Make it reachable: public domain

1. Click the service → **Settings** tab → **Networking**.
2. Under **Public Networking**, click **Generate Domain**.
3. Copy the domain, for example `crosscheck-production.up.railway.app`.

## 6. Check that it works

1. Open `https://<your-domain>/health` in a browser.
2. You should see something like `{"ok":true,"version":"0.1.0","protocol":2,"sessions":0}`.
   If you see that, the server is running.
3. In the service, open the **Deployments** tab. The latest one should say **Active**.
   Click **View logs** to see what the server prints.

If `/health` does not open: wait one minute and reload; check **Deployments** → **View logs**
for a red error; make sure the domain from step 5 exists.

## 7. Tell Crosscheck where the server is

Each pilot, once:

1. Start Crosscheck. On the start screen, find **Session server**.
2. Type `wss://<your-domain>/ws` (for example `wss://crosscheck-production.up.railway.app/ws`).
   Typing only the domain also works.
3. Click **Save**. Crosscheck remembers it on this computer.

Both pilots must use the same address. The address cannot be changed during a session.

For a headless start from a command line: `crosscheck create --server wss://<your-domain>/ws`.

## Logs

- **On the server (Railway):** one line per session event shows in the service's **Logs** tab
  (session created, pilot joined, desync, pilot lost and so on). Join codes are not printed.
  The full session logs, one file per session, are in the volume at `/data/logs`.
- **On each pilot's computer:** Crosscheck writes its own log to
  `%LOCALAPPDATA%\Crosscheck\logs` (paste that into the Windows Explorer address bar). After a
  session ends, the window shows this folder. After a test, ask each pilot to send the newest
  file from there. You then do not need access to Railway.

## Limits and safety

- Each internet address may create 10 sessions and make 30 join attempts per 10 minutes.
  More than that is refused with a message saying when to try again. There is no login yet
  (planned in CRO-19), so anyone who knows the address and a valid code can join a session.
  Share the code only with your first officer.
- If the server stops or restarts, both sims keep flying normally. They are just not synced
  until the pilots start a new session.
- Railway's Hobby plan bills by use. A server with no sessions costs very little.

## Settings (only if you need them)

Set these under **Variables**. None are needed for a normal setup.

| Variable | Default | Meaning |
|---|---|---|
| `LOG_DIR` | `logs` | Folder for session log files. Use `/data/logs` with the volume. |
| `CROSSCHECK_CREATE_LIMIT` | `10` | Sessions an address may create per 10 minutes. `0` = no limit. |
| `CROSSCHECK_JOIN_LIMIT` | `30` | Join attempts an address may make per 10 minutes. `0` = no limit. |
| `CROSSCHECK_TRUST_PROXY` | on at Railway | Use the visitor's real address from Railway's proxy for the limits above. |
