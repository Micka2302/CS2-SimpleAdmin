ALTER TABLE `sa_bans` CHANGE `player_steamid` `player_steamid` VARCHAR(64) NULL DEFAULT NULL;
UPDATE `sa_bans`
SET admin_steamid = '0'
WHERE admin_steamid NOT REGEXP '^[0-9]+$';
ALTER TABLE `sa_bans` CHANGE `admin_steamid` `admin_steamid` VARCHAR(64) NOT NULL;

ALTER TABLE `sa_mutes` CHANGE `player_steamid` `player_steamid` VARCHAR(64) NULL DEFAULT NULL;
UPDATE `sa_mutes`
SET admin_steamid = '0'
WHERE admin_steamid NOT REGEXP '^[0-9]+$';
ALTER TABLE `sa_mutes` CHANGE `admin_steamid` `admin_steamid` VARCHAR(64) NOT NULL;

ALTER TABLE `sa_warns` CHANGE `player_steamid` `player_steamid` VARCHAR(64) NULL DEFAULT NULL;
UPDATE `sa_warns`
SET admin_steamid = '0'
WHERE admin_steamid NOT REGEXP '^[0-9]+$';
ALTER TABLE `sa_warns` CHANGE `admin_steamid` `admin_steamid` VARCHAR(64) NOT NULL;

UPDATE `sa_admins`
SET player_steamid = '0'
WHERE player_steamid NOT REGEXP '^[0-9]+$';
ALTER TABLE `sa_admins` CHANGE `player_steamid` `player_steamid` VARCHAR(64) NULL DEFAULT NULL;

