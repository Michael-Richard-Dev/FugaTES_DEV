# DEV110 physical readback implementation plan

Goal: opening the official WinForms connects automatically to the existing private R165AL API and shows verified PostgreSQL state, without a demonstration launcher or a direct database connection.

Authorization/spec: HERA V4 FUGATES-DEV110-POSTGRESQL-LIVE-CONNECTION. Canonical source only. No commit, main merge, new workspace, VM change, database write or business POST.

Chosen design: retain PhysicalMonitorClient and its strict physical frame identity guard. The session owns the reader and a bounded local SSH tunnel lifecycle. Reuse only a locally verified exact SSH command; otherwise fail closed. Missing tunnel may be started with the existing identity and trusted host record. Never start or restart the remote backend. Main shell probes asynchronously and exposes verified identity, last success and a safe error. No cached row or in-memory fallback after failure.

Alternatives excluded: direct PostgreSQL access adds credential/provider exposure; a separate monitor or service contradicts the requested single application. A permanently running corporate service is out of scope.

- [x] Add negative/positive tests for connection status, identity rejection, failure/recovery, pinned tunnel arguments and no key transmission through an untrusted listener.
- [x] Extend the existing reader with connection state and a preparation hook; retain GET-only and the existing validation.
- [x] Add local tunnel management with strict existing SSH identity/host keys, bounded startup and ownership-aware cleanup; no remote commands.
- [x] Integrate automatic connection/status/retry into the official session and shell, clearing stale data on failure.
- [x] Run focal monitor regression and Release solution build. Review actual diff; no unrelated modules modified.
- [x] Preserve verified DEV110 v1.1.0 backup, publish v1.1.1 and update only reconciled desktop files and the local read profile.
- [x] Open, close and reopen the installed executable, demonstrate verified physical reads and controlled tunnel recovery, keep the final application visible.

Review focus: mismatched cluster/role; untrusted local port; SSH authentication failure; backend absent; key absent or rejected; asynchronous close; stale successful status; no accidental activation of carrier/ACM workers. Physical backend metadata remains PostgreSQL real / Guardian simulated and only the API's authorized integration is queried.
