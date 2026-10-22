# slop-sniper

Discord bot that checks posted images for OpenAI provenance signals (C2PA and SynthID) and scams.

## Commands

`Check image provenance` (message context menu) - Check a message's images and link previews. Only you see the result. Install the app on your account to use it anywhere. (Bot owner and `Provenance.UserAppUserIds`; Manage Messages in `GuildId`)

`/sniper status` - Show scanning status and counters. (Manage Messages)

`/sniper pause|resume [module]` - Stop or resume automatic scanning, including across restarts. (Manage Server)

`/sniper-reload` - Re-read `config.json` without restarting. (Bot owner)

`Report as scam` (message context menu) - Delete the post, time out its author and remember its images and link domains. (Moderate Members)

`Debug scam check` (message context menu) - Show how the scam checks score a message, without acting. (Bot owner, user install)

Scam alert buttons: Undo, Delete & time out, Not a scam. (Moderate Members)

Permissions are enforced by the bot; Server Settings > Integrations can only restrict them.

## Config

Copy `config.example.json` to `config.json` next to the binary.

`Token` - Your bot token. (Needs the Message Content intent if `GuildId` is set)

`GuildId` - Server to scan automatically and register `/sniper` in. (0 for user app only)

`LogChannelId` - Channel ID for reports and alerts. (Required if `GuildId` is set)

`ExcludedChannelIds` - Channel or category IDs that aren't scanned automatically.

`ScanBots` - Also scan messages from other bots. (Webhooks are always scanned)

`Provenance.Enabled` - OpenAI provenance checks. (Restart to change)

`Provenance.OpenAiApiKey` - OpenAI API key. (Only needs the Images permission)

`Provenance.UserAppUserIds` - User IDs that can use `Check image provenance` anywhere, besides the bot owner.

`Provenance.ReportMode` - `Private` (log channel), `Public` (reply to the image) or `Both`.

`Provenance.MaxImageMiB` - Skip larger images. (1-50)

`Provenance.RequestIntervalSeconds` - Minimum seconds between OpenAI requests.

`Provenance.QueueCapacity` - Messages waiting to be checked before new ones are dropped.

`Provenance.CacheHours` - Reuse the result for an identical file for this long. (0 to disable)

`Scams.Enabled` - Delete scam posts (images or links) in `GuildId` and time out the author. (Needs Manage Messages and Moderate Members; restart to change)

`Scams.BurstWindowSeconds` - Window for spotting the same post in several channels. (10-600)

`Scams.BurstChannels` - Act when the same images, or the same text with a link, appear in this many channels. (2-20)

`Scams.ShapeMinImages` - Image count that, with little text, looks like a scam post.

`Scams.DeleteMessages` - Delete the posts.

`Scams.TimeoutMinutes` - Timeout length. (0 to disable, max 40320)

`Scams.ExemptRoleIds` - Role IDs that are never acted on. (Moderators never are)

`Scams.HashDistance` - Max bits (of 64) an image can differ from a known scam image. (0-16)

`Scams.MaxKnownImages` - Known scam images to keep. (10-5000)

`Scams.TrustedDomains` - Domains never learned as scam domains, besides a built-in list of big sites.

Any setting can be overridden with a `SLOPSNIPER_` environment variable, e.g. `SLOPSNIPER_Provenance__OpenAiApiKey`.

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
