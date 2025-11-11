-- Revert player_steamid columns back to VARCHAR so we can safely store non-numeric Steam IDs (e.g. bot/console values)

ALTER TABLE `sa_bans`
    MODIFY `player_steamid` VARCHAR(64) NULL DEFAULT NULL;

ALTER TABLE `sa_mutes`
    MODIFY `player_steamid` VARCHAR(64) NOT NULL;

ALTER TABLE `sa_warns`
    MODIFY `player_steamid` VARCHAR(64) NOT NULL;

ALTER TABLE `sa_admins`
    MODIFY `player_steamid` VARCHAR(64) NOT NULL;
