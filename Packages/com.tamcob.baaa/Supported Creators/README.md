# Supported Creators

BAAA works with any VRCFury outfit on an avatar. A creator file adds what BAAA can't guess: where that creator's packs live. With one, BAAA also lists their pack prefabs before you place them, and texture quality covers the whole pack folder.

One `.json` file per creator. Put your own in `Assets/BAAA/Supported Creators`, so a BAAA update never replaces them.

```json
{
  "name": "Example Creator",
  "folders": ["Assets/Example Creator"],
  "notPacks": ["Shared", "Tools"],
  "untouched": ["Tools"],
  "noQuality": ["Shared"]
}
```

- `folders`: where the packs live. Every folder right inside one of these is one pack.
- `notPacks`: folders in there that aren't packs, like shared files or tools.
- `untouched`: folders BAAA never changes, not even their icons.
- `noQuality`: folders left out of texture size and compression.
