# Absynthium Steam Group Check

Absynthium_SteamGroupCheck is a CounterStrikeSharp plugin that grants configured admin flags to players who belong to one or more Steam groups. Flags are applied live for the current session and are not written to `addons/counterstrikesharp/configs/admins.json`.

## How It Works

When a player joins, the plugin queries the Steam Web API and compares the returned groups with your configured list. Any matching group's flags are granted; they are revoked when the player leaves or loses eligibility. Non-members can receive periodic reminder messages defined in the language file.

## Prerequisites

- MetaMod 1.12 (stable)
- CounterStrikeSharp running on .NET 8
- A Steam Web API key tied to the server's Steam account

## Installation

### Use the release ZIP (no build required)

1. Download the latest release archive from the [releases page](../../releases).
2. Extract `build/Absynthium_SteamGroupCheck.dll` from the ZIP.
3. Copy the DLL to your server's `addons/counterstrikesharp/plugins` folder.
4. Start the server once to generate the config at `addons/counterstrikesharp/configs/plugins/Absynthium_SteamGroupCheck/config.json` and the language file at `addons/counterstrikesharp/plugins/Absynthium_SteamGroupCheck/lang/en.json`, then edit them with your details.

### Build from source

1. Install the .NET 8 SDK.
2. Run:
   ```bash
   dotnet publish -c Release
   ```
3. Copy the resulting `Absynthium_SteamGroupCheck.dll` from `bin/Release/net8.0/publish/` to your server's `addons/counterstrikesharp/plugins` folder.
4. Start the server once to generate the config at `addons/counterstrikesharp/configs/plugins/Absynthium_SteamGroupCheck/config.json` and the language file at `addons/counterstrikesharp/plugins/Absynthium_SteamGroupCheck/lang/en.json`, then edit them with your details.

## Configuration

The main config lives at `addons/counterstrikesharp/configs/plugins/Absynthium_SteamGroupCheck/config.json` (created automatically if missing). When the DLL isn't inside `addons/counterstrikesharp/plugins`, a fallback `config/config.json` is written next to the binary:
```json
{
  "SteamApiKey": "REPLACE_ME",
  "Language": "en",
  "RequestTimeoutSeconds": 5,
  "CacheDurationSeconds": 120,
  "NonMemberAdIntervalMinutes": 5,
  "EnableDebugLogging": false,
  "Groups": [
    {
      "GroupId": "REPLACE_ME",
      "GrantedFlag": "@abs/membre",
      "Name": "Primary group",
      "Priority": 100
    },
    {
      "GroupId": "SECOND_GROUP_ID",
      "GrantedFlag": "@vip",
      "Name": "VIP",
      "Priority": 50
    }
  ]
}
```
- `SteamApiKey`: Steam Web API key.
- `Language`: language file to load from `addons/counterstrikesharp/plugins/Absynthium_SteamGroupCheck/lang/<code>.json` (next to the DLL).
- `RequestTimeoutSeconds`: HTTP request timeout in seconds.
- `CacheDurationSeconds`: cache duration for successful Steam API responses (only positive matches are cached).
- `NonMemberAdIntervalMinutes`: minutes between automatic prompts shown to non-members. Set to `0` to disable the broadcast.
- `EnableDebugLogging`: set to `true` for verbose traces.
- `Groups`: list of Steam groups to check.
  - `GroupId`: 64-bit Steam group ID (numeric string).
  - `GrantedFlag`: admin flag granted when the player is a member of that group. Multiple groups can grant different flags.
  - `Name`: optional label used in messages. If omitted, the group ID is displayed.
  - `Priority`: higher numbers win. The highest-priority group the player matches becomes their primary group, and flags are applied in priority order (ties follow config order).

### Language file

Messages live in `addons/counterstrikesharp/plugins/Absynthium_SteamGroupCheck/lang/en.json` (the `lang/` folder next to the DLL; if only a config copy exists it is migrated there):
```json
{
  "MemberDetected": "{red}Group check{/reset} You are detected as a member of: {MemberGroups}",
  "SteamIdUnavailable": "{red}Group check{/reset} Your SteamID isn't available yet.",
  "GroupCheckMember": "{red}Group check{/reset} You are a member of: {MemberGroups}",
  "GroupCheckNotMember": "{red}Group check{/reset} You are not a member of our groups. Visit {GroupUrls}",
  "NonMemberAdvertisement": "{red}Group check{/reset} Join our Steam groups to unlock rewards! Visit {GroupUrls}",
  "GroupCheckUnknown": "{red}Group check{/reset} Failed to determine group membership right now."
}
```
Supported placeholders: `{PlayerName}`, `{SteamId64}`, `{GroupId}` (primary group: highest-priority match, or highest-priority configured group if the player is unknown), `{GroupUrl}` (primary group), `{GroupList}`, `{GroupUrls}`, `{MemberGroups}`, `{MemberGroupUrls}`, `{Flag}`, `{Flags}` (ordered by priority).

### Chat colour tags

Messages support inline colour tags so you can match Counter-Strike chat colours. Tags are case-insensitive; opening tags apply the colour until another tag appears, and any closing tag like `{/reset}` returns to the default colour.

- Base colours: `{default}`, `{reset}`, `{white}`, `{green}`, `{lightgreen}`, `{lightyellow}`, `{yellow}`, `{lightblue}`, `{blue}`, `{darkblue}`, `{olive}`, `{lime}`, `{red}`, `{lightred}`, `{darkred}`, `{lightpurple}`, `{purple}`, `{magenta}`, `{grey}`, `{gray}`, `{gold}`, `{silver}`, `{orange}`.
- Team helpers: `{team}`, `{ct}`, `{counterterrorist}`, `{t}`, `{terrorist}`, `{spec}`, `{spectator}`.
- Any closing tag, e.g. `{/reset}` or `{/red}`, reverts to `{default}`.

Example: `{gold}[SGC]{/reset} {team}{PlayerName}{/reset}`.

## Commands and Logging

- `css_steamgroupcheck_reload`: reloads the configuration and language files, clears the cache, and re-applies membership checks to connected players. The console replies `Absynthium_SteamGroupCheck config reloaded`.
- `!group_check`: players can type in chat to see whether they are in any configured group. Non-members get the join link from the language file.

Runtime logging behaves as follows:
- With `EnableDebugLogging = false`, only important events are logged (grants, revokes, configuration issues, API errors).
- With `EnableDebugLogging = true`, additional debug entries show request flow, player slot resolution, and raw stats to help diagnose problems.

Server logs confirm flag changes:
- `Granted <flag> to <SteamId>` when a member connects (per matching group/flag).
- `Revoked <flag> from <SteamId>` when the player disconnects or loses eligibility.
Flags are temporary and are not written to `addons/counterstrikesharp/configs/admins.json`.

## Testing the Plugin

### Running a local server

1. Ensure MetaMod and CounterStrikeSharp are installed on your dedicated server.
2. Build the plugin and copy `Absynthium_SteamGroupCheck.dll` as described above. Start the server once so the configuration and language files are generated, then adjust them to your needs.
3. Launch a local test server with the plugin loaded. A typical command is:
   ```bash
   ./cs2 -dedicated -insecure +sv_lan 1 +map de_dust2
   ```
   The plugin will load automatically from `addons/counterstrikesharp/plugins`.

### Inspecting logs

1. Start the server and connect with a Steam account.
2. Watch the server console or log file:
   - `Granted <flag> to <SteamId>` confirms permissions were applied and the player also receives an in-game chat message.
   - `Revoked <flag> from <SteamId>` shows the flags were cleared when the player disconnects or is not a member.

### Simulating a non-member

To test flag removal, connect with an account that is not in any configured Steam group. You can use a secondary Steam account or temporarily change a `GroupId` in the configuration to a group the test account does not belong to. When the account connects, the log should show `Revoked <flag> from <SteamId>`, confirming that non-members do not retain admin permissions.
