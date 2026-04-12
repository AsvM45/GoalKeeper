-- GoalKeeper Ultimate Productivity System
-- SQLite Schema Reference
-- Applied automatically by ScreenTimeLogger.cs on first run

PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

-- ────────────────────────────────────────────────────────────────────────────
-- Core screen-time tracking
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS ScreenTimeLog (
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp      TEXT    NOT NULL DEFAULT (datetime('now')),
    AppName        TEXT    NOT NULL,
    WindowTitle    TEXT,
    Category       TEXT,               -- 'productive' | 'distracting' | 'neutral'
    DurationSeconds INTEGER DEFAULT 0,
    SessionId      TEXT                -- groups one continuous usage session
);

CREATE INDEX IF NOT EXISTS idx_stl_timestamp ON ScreenTimeLog(Timestamp);
CREATE INDEX IF NOT EXISTS idx_stl_appname   ON ScreenTimeLog(AppName);
CREATE INDEX IF NOT EXISTS idx_stl_category  ON ScreenTimeLog(Category);

-- ────────────────────────────────────────────────────────────────────────────
-- Budget system (shared time pools & launch limits)
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS Budgets (
    Id             INTEGER PRIMARY KEY AUTOINCREMENT,
    Category       TEXT    NOT NULL UNIQUE,
    AllowedSeconds INTEGER NOT NULL DEFAULT 3600,
    UsedSeconds    INTEGER NOT NULL DEFAULT 0,
    MaxLaunches    INTEGER NOT NULL DEFAULT -1,  -- -1 = unlimited
    UsedLaunches   INTEGER NOT NULL DEFAULT 0,
    SessionMinutes INTEGER NOT NULL DEFAULT 5,   -- per-session cap after friction
    FrictionSeconds INTEGER NOT NULL DEFAULT 20, -- pause overlay duration
    ResetTime      TEXT    NOT NULL DEFAULT '00:00',  -- HH:MM daily reset
    LastResetDate  TEXT
);

-- ────────────────────────────────────────────────────────────────────────────
-- App/site category membership
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS CategoryRules (
    Id       INTEGER PRIMARY KEY AUTOINCREMENT,
    Pattern  TEXT    NOT NULL,   -- process name or domain pattern (supports * wildcard)
    Category TEXT    NOT NULL,   -- must match a Budgets.Category or 'whitelist'/'blacklist'
    RuleType TEXT    NOT NULL    -- 'app' | 'domain' | 'window_title'
);

CREATE INDEX IF NOT EXISTS idx_cr_category ON CategoryRules(Category);

-- ────────────────────────────────────────────────────────────────────────────
-- Active / in-progress session tracking
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS ActiveSessions (
    AppName     TEXT    PRIMARY KEY,
    StartedAt   TEXT    NOT NULL DEFAULT (datetime('now')),
    ExpiresAt   TEXT    NOT NULL,        -- when auto-close fires
    SessionId   TEXT    NOT NULL
);

-- ────────────────────────────────────────────────────────────────────────────
-- Persistent key-value system state
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS SystemState (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);
-- Pre-populated keys:
--   IsArmed              : '0' | '1'
--   AuditModeStart       : ISO datetime string
--   ActiveMode           : 'none' | 'nuclear_offline' | 'nuclear_whitelist' | 'nuclear_strict'
--   NuclearEndTimeEpoch  : Unix epoch integer as text
--   DowntimeStart        : 'HH:MM'
--   DowntimeEnd          : 'HH:MM'
--   DowntimeEnabled      : '0' | '1'
--   PartnerPasswordHash  : bcrypt hash
--   AIEnabled            : '0' | '1'
--   AIApiKey             : encrypted key
--   UserGoals            : JSON array of goal strings

INSERT OR IGNORE INTO SystemState VALUES ('IsArmed',             '0');
INSERT OR IGNORE INTO SystemState VALUES ('AuditModeStart',      datetime('now'));
INSERT OR IGNORE INTO SystemState VALUES ('ActiveMode',          'none');
INSERT OR IGNORE INTO SystemState VALUES ('NuclearEndTimeEpoch', '0');
INSERT OR IGNORE INTO SystemState VALUES ('DowntimeEnabled',     '0');
INSERT OR IGNORE INTO SystemState VALUES ('DowntimeStart',       '22:00');
INSERT OR IGNORE INTO SystemState VALUES ('DowntimeEnd',         '07:00');
INSERT OR IGNORE INTO SystemState VALUES ('PartnerPasswordHash', '');
INSERT OR IGNORE INTO SystemState VALUES ('AIEnabled',           '0');
INSERT OR IGNORE INTO SystemState VALUES ('AIApiKey',            '');
INSERT OR IGNORE INTO SystemState VALUES ('UserGoals',           '[]');

-- ────────────────────────────────────────────────────────────────────────────
-- AI judgment cache (avoid re-querying Groq for same content)
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS AICache (
    UrlOrApp    TEXT    PRIMARY KEY,
    Judgment    TEXT    NOT NULL,    -- 'allow' | 'block' | 'distraction'
    Confidence  REAL,
    Reason      TEXT,
    Category    TEXT,
    CachedAt    TEXT    NOT NULL DEFAULT (datetime('now')),
    UserOverride TEXT               -- 'allow' | 'block' | NULL (user correction)
);

-- ────────────────────────────────────────────────────────────────────────────
-- Pickup / context-switch tracking (Screen Time "Pickups" equivalent)
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS PickupLog (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp   TEXT    NOT NULL DEFAULT (datetime('now')),
    FromApp     TEXT,
    ToApp       TEXT,
    ToCategory  TEXT
);

CREATE INDEX IF NOT EXISTS idx_pl_timestamp ON PickupLog(Timestamp);

-- ────────────────────────────────────────────────────────────────────────────
-- Typing challenge completion tokens (HMAC-based, single-use)
-- ────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS ChallengeTokens (
    Token     TEXT    PRIMARY KEY,
    CreatedAt TEXT    NOT NULL DEFAULT (datetime('now')),
    UsedAt    TEXT
);
