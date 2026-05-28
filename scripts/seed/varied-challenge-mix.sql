-- 15 NEW challenges for game 1, each with a distinct (Type, Category)
-- pair, so the player scoreboard / kind-switcher / category bands have
-- real variety to render.
--
-- Coverage matrix (none of these overlap each other on (Type, Category)):
--   StaticAttachment  × Crypto / Forensics / Reverse        (3)
--   StaticContainer   × Web / Pwn / Hardware                (3)
--   DynamicAttachment × Pwn / Crypto / Mobile               (3)
--   DynamicContainer  × Web / Crypto / Blockchain / AI      (4)
--   AttackDefense     × PPC                                  (1)
--   KingOfTheHill     × Pentest                              (1)
--                                                     total = 15
--
-- ContainerImage on container-type rows reuses the existing demo image so
-- they can launch without a fresh build; FlagTemplate on dynamic types
-- is a placeholder that won't actually match a submission (these are
-- showcase entries, not playable). All inserted as IsEnabled=true so
-- they appear immediately on the live game.
--
-- Idempotency: the SELECT at the bottom names every Title, so re-running
-- this script produces duplicate rows (no ON CONFLICT clause). To re-seed
-- cleanly, DELETE the rows by Title first, or only run once per DB.
-- Apply to a deploy via:
--   docker exec <postgres> psql -U gzctf -d gzctf -f /path/to/this.sql

INSERT INTO "GameChallenges" (
    "Title", "Content", "GameId", "Category", "Type", "OriginalScore",
    "MinScoreRate", "Difficulty",
    "IsEnabled", "EnableTrafficCapture", "DisableBloodBonus", "SubmissionLimit",
    "ReviewStatus", "BuildStatus", "AdAllowEgress", "AdAllowSelfReset",
    "ContainerImage", "FlagTemplate", "ExposePort"
) VALUES
  -- StaticAttachment × 3 categories
  ('Caesar Last Words',       'A short ciphertext. Find the shift.',                       1,  1, 0,  100, 0.25, 5, true, false, false, 0, 0, 0, false, true, NULL, NULL, NULL),
  ('Whispers in the EXIF',    'A photograph. Something hides in the metadata.',            1,  6, 0,  150, 0.25, 5, true, false, false, 0, 0, 0, false, true, NULL, NULL, NULL),
  ('Strings of Doom',         'A small ELF. ``strings`` is a hint, not the answer.',       1,  4, 0,  120, 0.25, 5, true, false, false, 0, 0, 0, false, true, NULL, NULL, NULL),

  -- DynamicAttachment × 3 categories
  ('Stack Roulette',          'A vulnerable binary. The flag is regenerated per team.',    1,  2, 2,  200, 0.25, 5, true, false, false, 0, 0, 0, false, true, NULL, 'flag{stack_[GUID]}', NULL),
  ('RSA Variations',          'Each team gets their own modulus. Recover the message.',    1,  1, 2,  250, 0.25, 5, true, false, false, 0, 0, 0, false, true, NULL, 'flag{rsa_[GUID]}', NULL),
  ('APK in Pieces',           'A signed APK with per-team obfuscation. Reverse and submit.', 1,  8, 2,  300, 0.25, 5, true, false, false, 0, 0, 0, false, true, NULL, 'flag{apk_[GUID]}', NULL),

  -- StaticContainer × 3 categories
  ('Login Lapse',             'Shared web target. A logic flaw in the auth flow.',         1,  3, 1,  180, 0.25, 5, true, false, false, 0, 0, 0, false, true, 'gzctf/echo-http:test', NULL, 80),
  ('Heap Habits',             'A simple heap chal. Same binary for everyone.',             1,  2, 1,  220, 0.25, 5, true, false, false, 0, 0, 0, false, true, 'gzctf/echo-http:test', NULL, 80),
  ('UART Whisper',            'Emulated serial console. Listen carefully.',                1,  7, 1,  200, 0.25, 5, true, false, false, 0, 0, 0, false, true, 'gzctf/echo-http:test', NULL, 80),

  -- DynamicContainer × 4 categories
  ('SQLi Sandbox',            'Per-team DB. Drain the flag table.',                        1,  3, 3,  220, 0.25, 5, true, false, false, 0, 0, 0, false, true, 'gzctf/echo-http:test', 'flag{sqli_[GUID]}', 80),
  ('Hashstorm',               'Per-team salt. Reverse the hashing pipeline.',              1,  1, 3,  260, 0.25, 5, true, false, false, 0, 0, 0, false, true, 'gzctf/echo-http:test', 'flag{hash_[GUID]}', 80),
  ('Wallet Watcher',          'A toy chain. Drain the contract, take the flag.',           1,  5, 3,  300, 0.25, 5, true, false, false, 0, 0, 0, false, true, 'gzctf/echo-http:test', 'flag{wallet_[GUID]}', 80),
  ('Prompt Probe',            'An LLM gate. Coax it into spilling the flag.',              1, 10, 3,  280, 0.25, 5, true, false, false, 0, 0, 0, false, true, 'gzctf/echo-http:test', 'flag{prompt_[GUID]}', 80),

  -- AttackDefense (additional category beyond the existing Web ones)
  ('A&D — RaceCmd (PPC)',     'High-throughput command parser. Patch the parser, defend the flag.', 1,  9, 4,    0, 0.25, 5, true, false, false, 0, 0, 0, true,  true, 'gzctf/echo-http:test', NULL, 80),

  -- KingOfTheHill (additional category)
  ('KotH — Admin Panel',      'A shared admin login. First to plant their token in /koth/king wins the tick.', 1, 11, 5,    0, 0.25, 5, true, false, false, 0, 0, 0, true,  true, 'gzctf/echo-http:test', NULL, 80);

SELECT 'inserted ' || COUNT(*) || ' new challenges' AS result
  FROM "GameChallenges" WHERE "GameId"=1 AND "Title" IN (
    'Caesar Last Words','Whispers in the EXIF','Strings of Doom',
    'Stack Roulette','RSA Variations','APK in Pieces',
    'Login Lapse','Heap Habits','UART Whisper',
    'SQLi Sandbox','Hashstorm','Wallet Watcher','Prompt Probe',
    'A&D — RaceCmd (PPC)','KotH — Admin Panel');
