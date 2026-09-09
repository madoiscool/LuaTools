# Manifest sources as JSON

Extra manifest sources can be declared in `.json` files under
`%AppData%\LuaToolsGui\sources\`. A source declared this way appears as a row on the **Add** page and
installs through the same pipeline as any other.

Settings → *Manifest sources* lists the files found, what each contributed, and why anything was
refused. Files are re-read whenever that page is opened, so there is nothing to restart.

## Why

The sources the app can fetch from are otherwise fixed at build time. Following a repo that moved, or
adding a community one, means cutting a release. This makes it a line of JSON.

## Data only

A file can say **where** manifests are fetched from. It cannot supply code, a binary, or a fetch routine
of its own: it names one of the shapes the app already knows how to consume, and the app does the
fetching. Installing one of these files cannot execute anything.

## Format

`%AppData%\LuaToolsGui\sources\example.json`:

```json
{
  "schema": 1,
  "name": "Example sources",
  "author": "you",
  "sources": [
    {
      "name": "example-zip",
      "displayName": "Example",
      "kind": "manifestZip",
      "url": "https://raw.githubusercontent.com/someone/some-repo/main/{appid}.zip",
      "mirrors": ["https://cdn.jsdelivr.net/gh/someone/some-repo@main/{appid}.zip"],
      "badge": "Free"
    }
  ]
}
```

| Field | |
|---|---|
| `kind` | `manifestZip` — one `<appid>.zip` holding the lua and its `.manifest` files.<br>`luaFile` — one `<appid>.lua`, entitlements and depot keys only. |
| `url` | Must be `https` and contain `{appid}`. |
| `mirrors` | Tried in order when the primary is unreachable. Optional. |
| `displayName` | Row label. Defaults to `name`. |
| `badge` | Short label on the row. Cosmetic. Optional. |

A source is refused, with the reason shown in Settings, if it uses a name the app already uses, is not
`https`, has no `{appid}`, or names a `kind` that does not exist. One bad entry never takes the file's
good ones down with it.

GitHub urls go through the app's existing mirror fallback, the availability check included. A host that
refuses `HEAD` is probed with a one-byte ranged `GET` instead.
