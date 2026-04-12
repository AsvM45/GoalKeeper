namespace ServiceEngine.Core;

/// <summary>
/// Fallback schema embedded in the binary for post-install use when the
/// Database/schema.sql file is not present relative to the executable.
/// </summary>
internal static class EmbeddedSchema
{
    internal const string Sql = @"
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

CREATE TABLE IF NOT EXISTS ScreenTimeLog (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp       TEXT    NOT NULL DEFAULT (datetime('now')),
    AppName         TEXT    NOT NULL,
    WindowTitle     TEXT,
    Category        TEXT,
    DurationSeconds INTEGER DEFAULT 0,
    SessionId       TEXT
);

CREATE INDEX IF NOT EXISTS idx_stl_timestamp ON ScreenTimeLog(Timestamp);
CREATE INDEX IF NOT EXISTS idx_stl_appname   ON ScreenTimeLog(AppName);
CREATE INDEX IF NOT EXISTS idx_stl_category  ON ScreenTimeLog(Category);

CREATE TABLE IF NOT EXISTS Budgets (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Category        TEXT    NOT NULL UNIQUE,
    AllowedSeconds  INTEGER NOT NULL DEFAULT 3600,
    UsedSeconds     INTEGER NOT NULL DEFAULT 0,
    MaxLaunches     INTEGER NOT NULL DEFAULT -1,
    UsedLaunches    INTEGER NOT NULL DEFAULT 0,
    SessionMinutes  INTEGER NOT NULL DEFAULT 5,
    FrictionSeconds INTEGER NOT NULL DEFAULT 20,
    ResetTime       TEXT    NOT NULL DEFAULT '00:00',
    LastResetDate   TEXT
);

CREATE TABLE IF NOT EXISTS CategoryRules (
    Id       INTEGER PRIMARY KEY AUTOINCREMENT,
    Pattern  TEXT    NOT NULL,
    Category TEXT    NOT NULL,
    RuleType TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_cr_category ON CategoryRules(Category);

CREATE TABLE IF NOT EXISTS ActiveSessions (
    AppName   TEXT PRIMARY KEY,
    StartedAt TEXT NOT NULL DEFAULT (datetime('now')),
    ExpiresAt TEXT NOT NULL,
    SessionId TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS SystemState (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);

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

CREATE TABLE IF NOT EXISTS AICache (
    UrlOrApp     TEXT PRIMARY KEY,
    Judgment     TEXT NOT NULL,
    Confidence   REAL,
    Reason       TEXT,
    Category     TEXT,
    CachedAt     TEXT NOT NULL DEFAULT (datetime('now')),
    UserOverride TEXT
);

CREATE TABLE IF NOT EXISTS PickupLog (
    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
    Timestamp  TEXT NOT NULL DEFAULT (datetime('now')),
    FromApp    TEXT,
    ToApp      TEXT,
    ToCategory TEXT
);

CREATE INDEX IF NOT EXISTS idx_pl_timestamp ON PickupLog(Timestamp);

CREATE TABLE IF NOT EXISTS ChallengeTokens (
    Token     TEXT PRIMARY KEY,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UsedAt    TEXT
);
";
}
