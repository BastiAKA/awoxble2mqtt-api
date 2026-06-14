-- Seeds the runtime-tunable defaults into app_settings. Mirrors what the API seeds on first start
-- (EnsureDefaultAsync) — run it to pre-populate the values so they're visible/editable before the API
-- has ever started. Idempotent: INSERT IGNORE keeps any value you've already changed.
-- Run AFTER tables/03_app_settings.sql (CreateDBEnv.ps1 does this in order). See docs/CONFIGURATION.md.
USE AWOXHomeDB;

INSERT IGNORE INTO app_settings (`Key`, Value, Description, UpdatedUtc) VALUES
  ('ble.poll_interval_seconds', '5',
   'BLE status poll/keepalive interval in seconds (min 1). Takes effect on the next bulb (re)connect.',
   UTC_TIMESTAMP(6)),
  ('ble.offline_after_seconds', '600',
   'Seconds since a lamp was last seen before it counts as OFFLINE ("safe off"). Long on purpose — lamps advertise intermittently; only a real power-off should read as offline.',
   UTC_TIMESTAMP(6)),
  ('ble.relay_verify_enabled', 'true',
   'Var1 relay-verify master switch: relay a command through an already-held same-mesh node and confirm via the target''s advert, instead of always reconnecting directly. Off = pure direct-connect.',
   UTC_TIMESTAMP(6)),
  ('ble.relay_verify_timeout_ms', '5000',
   'Milliseconds to wait for the target''s confirming advert after a relayed command before declaring the relay path unreachable and falling back to a direct reconnect + resend.',
   UTC_TIMESTAMP(6)),
  ('ble.relay_reachable_ttl_seconds', '30',
   'Seconds a CONFIRMED relay path is trusted before re-confirming. Keep short: the fast path relays WITHOUT a check, so a silently-broken path drives the lamp blind for this window. 0/negative = verify every command.',
   UTC_TIMESTAMP(6)),
  ('ble.relay_unreachable_ttl_seconds', '120',
   'Seconds a FAILED relay path is trusted before re-probing. Short, because relay failures are usually transient (congestion, lamp briefly off) — a negative verdict must not stick until restart.',
   UTC_TIMESTAMP(6)),
  ('ble.advert_fast_poll_ms', '400',
   'Advert-scan poll period (ms) WHILE a confirmation is awaited (relay-verify in flight). Lamps advertise a change instantly; at the normal multi-second poll we''d read BlueZ''s cache too late and miss it. Idle, the scan stays on ble.poll_interval_seconds.',
   UTC_TIMESTAMP(6)),
  ('ble.connect_settle_ms', '600',
   'Pause (ms) after stopping LE discovery before a cold connect (else le-connection-abort-by-local). Was 1.5s for the flaky Pi-3 onboard radio; the BT500 dongle needs far less. Paid on every cold connect — dial down carefully.',
   UTC_TIMESTAMP(6)),
  ('ble.max_connections', '2',
   'Max held BLE sessions, one per mesh. Lets commands for different meshes run concurrently instead of reconnecting on every mesh switch; the command queue drains up to this many meshes in parallel. 1 = legacy single-session behaviour. Kept low for the flaky Pi dongle.',
   UTC_TIMESTAMP(6));
