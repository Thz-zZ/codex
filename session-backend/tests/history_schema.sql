-- Schema and migration metadata only; no user conversation data.
BEGIN;
CREATE TABLE _sqlx_migrations (
    version BIGINT PRIMARY KEY,
    description TEXT NOT NULL,
    installed_on TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
    success BOOLEAN NOT NULL,
    checksum BLOB NOT NULL,
    execution_time BIGINT NOT NULL
);
CREATE TABLE thread_turns (
    thread_id TEXT NOT NULL,
    turn_id TEXT NOT NULL,
    rollout_ordinal INTEGER NOT NULL,
    status TEXT NOT NULL,
    error_json TEXT,
    started_at INTEGER,
    completed_at INTEGER,
    duration_ms INTEGER,
    first_user_item_id TEXT,
    final_agent_item_id TEXT, rollout_byte_offset INTEGER, rollout_end_ordinal INTEGER, rollout_end_byte_offset INTEGER,
    PRIMARY KEY (thread_id, turn_id)
);
CREATE TABLE thread_items (
    thread_id TEXT NOT NULL,
    turn_id TEXT NOT NULL,
    item_id TEXT NOT NULL,
    rollout_ordinal INTEGER NOT NULL,
    created_at_ms INTEGER NOT NULL,
    item_json TEXT NOT NULL, item_type TEXT NOT NULL DEFAULT '', updated_at_ordinal INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (thread_id, turn_id, item_id)
);
CREATE TABLE thread_history_projection_state (
    thread_id TEXT PRIMARY KEY,
    next_rollout_byte_offset INTEGER NOT NULL,
    next_rollout_ordinal INTEGER NOT NULL
);
CREATE TABLE thread_realtime_items (
    thread_id TEXT NOT NULL,
    item_id TEXT NOT NULL,
    rollout_ordinal INTEGER NOT NULL,
    created_at_ms INTEGER NOT NULL,
    item_type TEXT NOT NULL,
    item_json TEXT NOT NULL,
    PRIMARY KEY (thread_id, item_id)
);
CREATE UNIQUE INDEX idx_thread_turns_page
    ON thread_turns(thread_id, rollout_ordinal);
CREATE UNIQUE INDEX idx_thread_items_page
    ON thread_items(thread_id, rollout_ordinal);
CREATE INDEX idx_thread_items_by_turn_page
    ON thread_items(thread_id, turn_id, rollout_ordinal);
CREATE INDEX idx_thread_items_user_messages
    ON thread_items(thread_id, rollout_ordinal)
    WHERE item_type = 'userMessage';
CREATE INDEX idx_thread_items_updated_page
    ON thread_items(thread_id, updated_at_ordinal);
CREATE INDEX idx_thread_items_by_turn_updated_page
    ON thread_items(thread_id, turn_id, updated_at_ordinal);
CREATE UNIQUE INDEX idx_thread_realtime_items_page
    ON thread_realtime_items(thread_id, rollout_ordinal);
CREATE INDEX idx_thread_realtime_items_boundary
    ON thread_realtime_items(thread_id, rollout_ordinal)
    WHERE item_type IN ('realtime_session_started', 'realtime_session_closed');
CREATE INDEX idx_thread_turns_end_page
    ON thread_turns(thread_id, rollout_end_ordinal, turn_id)
    WHERE rollout_end_ordinal IS NOT NULL;
CREATE TRIGGER thread_realtime_items_projection_cleanup
    AFTER DELETE ON thread_history_projection_state
BEGIN
    DELETE FROM thread_realtime_items WHERE thread_id = OLD.thread_id;
END;
INSERT INTO _sqlx_migrations VALUES (1,'thread history','2026-08-28 01:52:17',1,X'3d81349432ecbf6e5255dbe0a075d27811144fd3c32941ac1cbba640a83e278fa562309fb3b226964ee4251e97b4b513',1606500);
INSERT INTO _sqlx_migrations VALUES (2,'thread items item type','2026-08-28 01:52:17',1,X'1461cffa3015c335f993c4afeecb729714fc741d663038ebe18288b968aef006649eb1ef3a0716ce47fe6d08c06045a5',658400);
INSERT INTO _sqlx_migrations VALUES (3,'turn rollout positions','2026-08-28 01:52:17',1,X'cce3adb187490e2822a759c2668337cb277656f1fc1a1713542a4f15d23fd5e7e444328a58be7906067006fac870a574',1039600);
INSERT INTO _sqlx_migrations VALUES (4,'thread items updated at ordinal','2026-08-28 01:52:17',1,X'6a2824bee2b0670c6bb3bb5ae990f015863c5f6548540834eddcde94c470d6affa040a6e1e50ab81d75f0a149d19bb77',856600);
INSERT INTO _sqlx_migrations VALUES (5,'thread realtime items','2026-08-28 01:52:17',1,X'bdfaf405f3b733cf7455ca5aa0afae13869fd2ad2cf75245e6e268b9b110f331d3d69b01224bffa66d1107ff938e9d6b',781300);
INSERT INTO _sqlx_migrations VALUES (6,'thread turn ends','2026-08-28 01:52:17',1,X'7ae052fd42a8ab1e7d0fe33127157b9cf2552ab1c8010a3adeca49b9dd314163dc19663639a4a6bf479b9f25d1a5a16a',576400);
COMMIT;