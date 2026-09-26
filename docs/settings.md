# Capability tier settings

Open **Settings** in the local web console to change the model and effort for a Codex or Pi capability tier. The default table remains built in. Each changed tier is stored in `<state>/tier-map.json`; the file is private to the local user and is replaced atomically on save. Reset a row or all rows to remove overrides.

The new agent form shows each tier's effective model and effort. The daemon resolves that choice when it accepts a job. Existing jobs and follow-ups that inherit their parent's selection keep their stored model and effort. A follow-up that explicitly selects a tier uses the current mapping.

When the daemon has a cached model catalog, Settings offers its models as a dropdown and rejects unavailable choices with a CLI upgrade hint. With no known catalog, enter a model slug as text. Admission still checks the discovered catalog when it becomes available.

The authenticated `GET /api/settings/tiers` endpoint returns the effective and default tables plus cached catalogs. `PUT /api/settings/tiers` accepts `{ "backend": "codex", "tier": "xhigh", "model": "gpt-6-sol", "effort": "xhigh" }`. Omit `model` to reset one tier, or send `{ "reset_all": true }` to reset all. Both endpoints require the console bearer token; writes also require the console Origin.
