# slop-sniper

Discord bot that checks posted images for slop and scams. Each feature is a module that can be turned on or off.

## Commands

`Check image provenance` (message context menu) - Check a message's images and link previews for OpenAI provenance signals (C2PA and SynthID). Only you see the result. Install the app on your account to use it anywhere. (Bot owner and `Provenance.UserAppUserIds`; Manage Messages in `GuildId`)

`/sniper status` - Show scanning status and counters. (Manage Messages)

`/sniper pause|resume` - Stop or resume automatic scanning, including across restarts. (Manage Messages)

`/sniper-reload` - Re-read `config.json` without restarting. Works anywhere, including DMs with the bot. (Bot owner)

Scam alerts have an Undo button that lifts the timeout. (Moderate Members)

`/sniper` permissions can be changed in Server Settings > Integrations. Changing `GuildId` removes the commands from the previous server on the next start.

## Config

Copy `config.example.json` to `config.json` next to the binary.

`Token` - Your bot token. (Needs the Message Content intent if `GuildId` is set)

`GuildId` - Server to scan automatically and register `/sniper` in. (0 for user app only)

`LogChannelId` - Channel ID for reports and alerts. (Required if `GuildId` is set)

`ExcludedChannelIds` - Channel or category IDs that aren't scanned automatically.

`ScanBots` - Also scan messages from other bots. (Webhooks are always scanned)

### Provenance

`Provenance.Enabled` - OpenAI provenance checks, automatic in `GuildId` and manual anywhere. (Restart to change)

`Provenance.OpenAiApiKey` - OpenAI API key. (Only needs the Images permission)

`Provenance.UserAppUserIds` - User IDs that can use `Check image provenance` anywhere, besides the bot owner.

`Provenance.ReportMode` - `Private` (log channel), `Public` (reply to the image) or `Both`.

`Provenance.MaxImageMiB` - Skip larger images. (1-50)

`Provenance.RequestIntervalSeconds` - Minimum seconds between OpenAI requests.

`Provenance.QueueCapacity` - Messages waiting to be checked before new ones are dropped.

`Provenance.CacheHours` - Reuse the result for an identical file for this long. (0 to disable)

### ScamImages

Catches hijacked accounts posting the same scam screenshots across channels: deletes every copy, times out the author and alerts the log channel. Moderators, the owner, bots and webhooks are never acted on. The bot needs Manage Messages and Moderate Members, and its role must be above the members it times out.

`ScamImages.Enabled` - Scam image detection in `GuildId`. (Restart to change)

`ScamImages.BurstWindowSeconds` - How long after a post the same images elsewhere count as a burst. (10-600)

`ScamImages.BurstChannels` - Act when the same images are posted to this many channels. (2-20; 2 is enough if the post looks like a scam post)

`ScamImages.ShapeMinImages` - Posts with at least this many images and little text look like scam posts.

`ScamImages.DeleteMessages` - Delete the posts.

`ScamImages.TimeoutMinutes` - Time out the author for this long. (0 to disable, max 40320)

`ScamImages.ExemptRoleIds` - Role IDs that are never acted on.

Any setting can be overridden with a `SLOPSNIPER_` environment variable, with `__` between section and key, e.g. `SLOPSNIPER_Provenance__OpenAiApiKey`.

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
