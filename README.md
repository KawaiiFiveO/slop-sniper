# slop-sniper

Discord bot that checks posted images for OpenAI provenance signals (C2PA and SynthID).

## Commands

`Check image provenance` (message context menu) - Check a message's images and link previews. Only you see the result. Install the app on your account to use it anywhere. (Bot owner and `UserAppUserIds`; Manage Messages in `GuildId`)

`/sniper status` - Show scanning status and counters. (Manage Messages)

`/sniper pause|resume` - Stop or resume automatic scanning, including across restarts. (Manage Messages)

`/sniper reload` - Re-read `config.json` without restarting. (Bot owner)

`/sniper` permissions can be changed in Server Settings > Integrations.

## Config

Copy `config.example.json` to `config.json` next to the binary.

`Token` - Your bot token. (Needs the Message Content intent if `GuildId` is set)

`GuildId` - Server to scan automatically and register `/sniper` in. (0 for user app only)

`OpenAiApiKey` - OpenAI API key. (Only needs the Images permission)

`LogChannelId` - Channel ID for reports and alerts. (Required if `GuildId` is set)

`UserAppUserIds` - User IDs that can use `Check image provenance` anywhere, besides the bot owner.

`ReportMode` - `Private` (log channel), `Public` (reply to the image) or `Both`.

`ExcludedChannelIds` - Channel or category IDs that aren't scanned automatically.

`ScanBots` - Also scan messages from other bots. (Webhooks are always scanned)

`MaxImageMiB` - Skip larger images. (1-50)

`RequestIntervalSeconds` - Minimum seconds between OpenAI requests.

`QueueCapacity` - Messages waiting to be checked before new ones are dropped.

`CacheHours` - Reuse the result for an identical file for this long. (0 to disable)

Any setting can be overridden with a `SLOPSNIPER_` environment variable, e.g. `SLOPSNIPER_OpenAiApiKey`.

## Deployment

Releases are built by GitHub Actions when a `v*` tag is pushed (`v1.0.0-rc1` style tags are pre-releases):

```
git tag v1.0.0 && git push origin v1.0.0
```

Install on Linux, from the bot directory.

```
curl -fL https://github.com/KawaiiFiveO/slop-sniper/releases/latest/download/slop-sniper-linux-x64.tar.gz | tar -xz
cp config.example.json config.json    # then fill it in
sudo ./install.sh                     # installs and starts the slop-sniper systemd service
```

Update with `./update.sh` (`--rollback` to undo, `--version <tag>` for a specific release). Logs: `journalctl -u slop-sniper -f`

Exit code `78`: invalid config (not restarted by systemd).

Stop the bot:

```
sudo systemctl stop slop-sniper                 # stops until the next reboot
sudo systemctl disable --now slop-sniper        # stops and stays off after reboots
sudo systemctl enable --now slop-sniper         # start again
```

Uninstall:

```
sudo systemctl disable --now slop-sniper
sudo rm -f /etc/systemd/system/slop-sniper.service /etc/systemd/system/slop-sniper.service.bak
sudo systemctl daemon-reload
rm -r ~/slop-sniper
```

## Build

```
dotnet publish SlopSniper/SlopSniper.csproj -p:PublishProfile=linux-x64
```
